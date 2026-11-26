using FieldService.Api.Auth;
using FieldService.Api.Contracts;
using FieldService.Api.Data;
using FieldService.Api.Domain;
using FieldService.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FieldService.Api.Controllers;

/// <summary>
/// Symulacja dyspozytora (trener na demo): anulowanie i przeniesienie wizyty, nowa wizyta, stan magazynu.
/// Każda zmiana wizyty podnosi jej wersję — kolejna komenda z telefonu ze starą wersją dostaje 409 (kompensacja).
/// Opcjonalny klucz w nagłówku <c>X-Dispatcher-Key</c> (<c>Dispatcher:Key</c>).
/// </summary>
[ApiController]
[Route("api/dispatcher")]
[AllowAnonymous]
[DispatcherKey]
[EnableRateLimiting(RateLimiting.DispatcherPolicy)]
[Produces("application/json")]
public sealed class DispatcherController(FieldServiceDbContext db, DemoClock clock, DemoSeeder seeder) : ControllerBase
{
    /// <summary>Wizyty na dziś (wszystkich albo jednego technika) z wersjami.</summary>
    [HttpGet("visits", Name = "GetDispatcherVisits")]
    [ProducesResponseType<IReadOnlyList<DispatcherVisitDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<DispatcherVisitDto>> Visits([FromQuery] string? technicianEmail, CancellationToken ct)
    {
        var (from, to) = clock.DayRange(clock.Today);
        var query = db.Visits.AsNoTracking().Include(v => v.WorkOrder).Where(v => v.PlannedStart >= from && v.PlannedStart < to);
        if (!string.IsNullOrWhiteSpace(technicianEmail))
        {
            var key = Technicians.Normalize(technicianEmail);
            query = query.Where(v => v.TechnicianEmails.Contains(key));
        }
        var visits = await query.OrderBy(v => v.PlannedStart).Take(500).ToListAsync(ct);
        return visits.Select(v => new DispatcherVisitDto
        {
            Id = v.Id, WorkOrderNumber = v.WorkOrder.Number, Status = v.Status, PlannedStart = v.PlannedStart,
            TechnicianEmails = v.TechnicianEmails, CustomerName = v.WorkOrder.CustomerName, Version = v.Version
        }).ToList();
    }

    /// <summary>R6: anulowanie wizyty (zakończonej nie można → 409 <c>Visit.AlreadyCompleted</c>).</summary>
    [HttpPost("visits/{id:guid}/cancel", Name = "CancelVisit")]
    [ProducesResponseType<DispatcherVisitDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Cancel(Guid id, [FromBody] CancelVisitRequest request, CancellationToken ct) =>
        ChangeAsync(id, v => v.Cancel(string.IsNullOrWhiteSpace(request.Reason) ? "Anulowana przez dyspozytora" : request.Reason.Trim(), clock.Now), ct);

    /// <summary>Przeniesienie wizyty na inny termin (tylko Planned).</summary>
    [HttpPost("visits/{id:guid}/reschedule", Name = "RescheduleVisit")]
    [ProducesResponseType<DispatcherVisitDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> Reschedule(Guid id, [FromBody] RescheduleVisitRequest request, CancellationToken ct) =>
        ChangeAsync(id, v => v.Reschedule(DemoClock.Microseconds(request.PlannedStart.ToUniversalTime()),
            DemoClock.Microseconds(request.PlannedEnd.ToUniversalTime())), ct);

    /// <summary>Nowa wizyta dla technika (albo brygady) z nowym zleceniem.</summary>
    [HttpPost("visits", Name = "CreateVisit")]
    [ProducesResponseType<DispatcherVisitDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public IActionResult Create([FromBody] CreateVisitRequest request)
    {
        var emails = new[] { request.TechnicianEmail, request.SecondTechnicianEmail }.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!.Trim()).ToList();
        if (emails.Count == 0 || !emails.All(Technicians.IsValidEmail))
            return this.Refuse(VisitErrors.Validation("Podaj poprawny e-mail technika (i opcjonalnie drugiego technika brygady)."));

        var start = request.PlannedStart is { } s ? DemoClock.Microseconds(s.ToUniversalTime()) : clock.Now.AddHours(1);
        var visit = seeder.CreateVisit(emails, start, start.AddMinutes(90), request.FaultDescription);
        return StatusCode(StatusCodes.Status201Created, ToDto(visit));
    }

    /// <summary>Stan części w magazynie (inwentaryzacja albo dostawa). Rezerwacje zostają.</summary>
    [HttpPut("stock/{partId}/{warehouseId}", Name = "SetStock")]
    [ProducesResponseType<StockLevelDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetStock(string partId, string warehouseId, [FromBody] SetStockRequest request, CancellationToken ct)
    {
        var stock = await db.PartStocks.FirstOrDefaultAsync(s => s.PartId == partId.ToUpperInvariant() && s.WarehouseId == warehouseId.ToUpperInvariant(), ct);
        if (stock is null) return this.Refuse(InventoryErrors.StockNotFound(partId, warehouseId));
        stock.SetOnHand(request.OnHand);
        await db.SaveChangesAsync(ct);
        var warehouse = await db.Warehouses.FindAsync([stock.WarehouseId], ct);
        return Ok(new StockLevelDto
        {
            PartId = stock.PartId, WarehouseId = stock.WarehouseId, WarehouseName = warehouse?.Name ?? stock.WarehouseId,
            OnHand = stock.OnHand, Reserved = stock.Reserved(clock.Now), Available = stock.Available(clock.Now)
        });
    }

    private async Task<IActionResult> ChangeAsync(Guid id, Func<Visit, ApiError?> change, CancellationToken ct)
    {
        var visit = await db.Visits.Include(v => v.WorkOrder).FirstOrDefaultAsync(v => v.Id == id, ct);
        if (visit is null) return this.Refuse(VisitErrors.NotFound(id));
        var error = change(visit);
        if (error is not null) return this.Refuse(error);
        visit.Version++;
        visit.UpdatedAt = clock.Now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return this.Refuse(VisitErrors.VersionConflict(visit.Version - 1, visit.Version));
        }
        return Ok(ToDto(visit));
    }

    private static DispatcherVisitDto ToDto(Visit v) => new()
    {
        Id = v.Id, WorkOrderNumber = v.WorkOrder.Number, Status = v.Status, PlannedStart = v.PlannedStart,
        TechnicianEmails = v.TechnicianEmails, CustomerName = v.WorkOrder.CustomerName, Version = v.Version
    };
}

/// <summary>Gdy ustawiono <c>Dispatcher:Key</c>, wymaga go w nagłówku <c>X-Dispatcher-Key</c>.</summary>
public sealed class DispatcherKeyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var expected = context.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<DispatcherOptions>>().CurrentValue.Key;
        if (string.IsNullOrEmpty(expected)) return;
        if (Secrets.FixedTimeEquals(context.HttpContext.Request.Headers["X-Dispatcher-Key"].ToString(), expected)) return;
        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Brak dostępu",
            Detail = "Nieprawidłowy lub brakujący nagłówek X-Dispatcher-Key."
        }) { StatusCode = StatusCodes.Status401Unauthorized };
    }
}
