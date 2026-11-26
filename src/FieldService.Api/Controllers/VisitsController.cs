using System.Text.Json;
using FieldService.Api.Auth;
using FieldService.Api.Contracts;
using FieldService.Api.Data;
using FieldService.Api.Domain;
using FieldService.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FieldService.Api.Controllers;

/// <summary>Wizyty zalogowanego technika: odczyt i komendy (Realizacja wizyty).</summary>
[ApiController]
[Route("api/visits")]
[Authorize]
[Produces("application/json")]
public sealed class VisitsController(FieldServiceDbContext db, DemoClock clock, IOptions<JsonOptions> json) : ControllerBase
{
    // Komendy (POST/PUT/DELETE): nagłówek If-Match (albo X-If-Match) z wersją wizyty (wymagany; brak → 428) i Idempotency-Key
    // (zalecany — aplikacja nadaje go raz przy zapisie w outboxie i powtarza przy każdym ponowieniu).

    /// <summary>Jak długo pamiętamy klucz idempotencji (potem ten sam klucz działa jak nowy).</summary>
    public static readonly TimeSpan IdempotencyKeyLifetime = TimeSpan.FromHours(24);

    /// <summary>Wizyty na dziś (strefa czasowa dnia pracy), od najwcześniejszej.</summary>
    /// <remarks>Odpowiedź ma <c>ETag</c>; ten sam ETag w <c>If-None-Match</c> → 304 bez treści.</remarks>
    /// <response code="200">Lista wizyt.</response>
    /// <response code="304">Lista się nie zmieniła.</response>
    [HttpGet("today", Name = "GetTodayVisits")]
    [DemoDelay]
    [ProducesResponseType<IReadOnlyList<VisitSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<IActionResult> Today(CancellationToken ct)
    {
        var email = User.Email();
        var (from, to) = clock.DayRange(clock.Today);
        var visits = await db.Visits.AsNoTracking().Include(v => v.WorkOrder)
            .Where(v => v.TechnicianEmails.Contains(email) && v.PlannedStart >= from && v.PlannedStart < to)
            .OrderBy(v => v.PlannedStart)
            .ToListAsync(ct);
        return this.OkWithETag(visits.Select(v => v.ToSummary(Request)).ToList());
    }

    /// <summary>Historia: zakończone i anulowane wizyty z poprzednich dni, od najnowszej (stronicowanie).</summary>
    /// <param name="page">Numer strony od 1.</param>
    /// <param name="pageSize">Rozmiar strony 1–100.</param>
    [HttpGet("history", Name = "GetVisitHistory")]
    [ProducesResponseType<PageDto<VisitHistoryItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<IActionResult> History([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var email = User.Email();
        var (todayStart, _) = clock.DayRange(clock.Today);
        var query = db.Visits.AsNoTracking()
            .Where(v => v.TechnicianEmails.Contains(email) && v.PlannedStart < todayStart
                        && (v.Status == VisitStatus.Completed || v.Status == VisitStatus.Cancelled));
        var total = await query.CountAsync(ct);
        var items = await query.Include(v => v.WorkOrder)
            .OrderByDescending(v => v.PlannedStart).ThenBy(v => v.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        return this.OkWithETag(new PageDto<VisitHistoryItemDto>
        {
            Items = items.Select(v => v.ToHistoryItem(Request)).ToList(), Page = page, PageSize = pageSize, TotalCount = total
        });
    }

    /// <summary>Szczegóły wizyty. Nagłówek <c>ETag</c> = wersja wizyty (do <c>If-Match</c> w komendach).</summary>
    /// <response code="200">Wizyta.</response>
    /// <response code="304">Wersja z <c>If-None-Match</c> jest aktualna.</response>
    /// <response code="404">Brak wizyty albo nie jest przypisana do technika.</response>
    [HttpGet("{id:guid}", Name = "GetVisit")]
    [ProducesResponseType<VisitDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var visit = await Load(id, tracking: false, ct);
        if (visit is null) return this.Refuse(VisitErrors.NotFound(id));
        var etag = ETags.ForVersion(visit.Version);
        Response.Headers.ETag = etag;
        if (Request.Headers.IfNoneMatch.ToString() == etag) return StatusCode(StatusCodes.Status304NotModified);
        return Ok(await DetailsAsync(visit, ct));
    }

    /// <summary>R1: rozpoczęcie wizyty (tylko Planned).</summary>
    [HttpPost("{id:guid}/start", Name = "StartVisit")]
    [ProducesResponseType<VisitDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public Task<IActionResult> Start(Guid id, [FromBody] StartVisitRequest? request, 
        [FromHeader(Name = "If-Match")] string? ifMatch, [FromHeader(Name = "X-If-Match")] string? xIfMatch, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct) =>
        ExecuteAsync(id, ifMatch ?? xIfMatch, idempotencyKey, request, v => v.Start(At(request?.StartedAt)), ct);

    /// <summary>R2: czynność w zakresie prac (tylko InProgress; po podpisie unieważnia podpis — R3).</summary>
    [HttpPost("{id:guid}/activities", Name = "RecordActivity")]
    [ProducesResponseType<VisitDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public Task<IActionResult> RecordActivity(Guid id, [FromBody] RecordActivityRequest request, 
        [FromHeader(Name = "If-Match")] string? ifMatch, [FromHeader(Name = "X-If-Match")] string? xIfMatch, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct) =>
        ExecuteAsync(id, ifMatch ?? xIfMatch, idempotencyKey, request, v => v.RecordActivity(request.Code ?? "", request.Description ?? "", At(request.RecordedAt)), ct);

    /// <summary>R2: zużyta część (ta sama część drugi raz zwiększa ilość; po podpisie unieważnia podpis — R3).</summary>
    [HttpPost("{id:guid}/parts", Name = "UsePart")]
    [ProducesResponseType<VisitDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> UsePart(Guid id, [FromBody] UsePartRequest request, 
        [FromHeader(Name = "If-Match")] string? ifMatch, [FromHeader(Name = "X-If-Match")] string? xIfMatch, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        var partId = request.PartId?.Trim().ToUpperInvariant() ?? "";
        if (!await db.Parts.AnyAsync(p => p.Id == partId, ct))
            return this.Refuse(VisitErrors.Validation($"Części {partId} nie ma w katalogu (GET api/parts)."));
        return await ExecuteAsync(id, ifMatch ?? xIfMatch, idempotencyKey, request, v => v.UsePart(partId, request.Quantity), ct);
    }

    /// <summary>R7: usunięcie omyłkowo dopisanej części (tylko InProgress; po podpisie unieważnia podpis — R3).</summary>
    [HttpDelete("{id:guid}/parts/{partId}", Name = "RemoveUsedPart")]
    [ProducesResponseType<VisitDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public Task<IActionResult> RemoveUsedPart(Guid id, string partId, 
        [FromHeader(Name = "If-Match")] string? ifMatch, [FromHeader(Name = "X-If-Match")] string? xIfMatch, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct) =>
        ExecuteAsync(id, ifMatch ?? xIfMatch, idempotencyKey, new { partId }, v => v.RemoveUsedPart(partId.Trim().ToUpperInvariant()), ct);

    /// <summary>R4: podpis klienta pod bieżącym zakresem prac (tylko InProgress, zakres niepusty).</summary>
    [HttpPut("{id:guid}/signature", Name = "SignVisit")]
    [ProducesResponseType<VisitDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public Task<IActionResult> Sign(Guid id, [FromBody] SignVisitRequest request, 
        [FromHeader(Name = "If-Match")] string? ifMatch, [FromHeader(Name = "X-If-Match")] string? xIfMatch, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct) =>
        ExecuteAsync(id, ifMatch ?? xIfMatch, idempotencyKey, request, v => v.SignByCustomer(request.SignerName ?? "", At(request.SignedAt)), ct);

    /// <summary>R5: zakończenie wizyty. Fixed wymaga ważnego podpisu i zamyka zlecenie; inne wyniki wymagają powodu.</summary>
    [HttpPost("{id:guid}/complete", Name = "CompleteVisit")]
    [ProducesResponseType<VisitDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public Task<IActionResult> Complete(Guid id, [FromBody] CompleteVisitRequest request, 
        [FromHeader(Name = "If-Match")] string? ifMatch, [FromHeader(Name = "X-If-Match")] string? xIfMatch, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct) =>
        ExecuteAsync(id, ifMatch ?? xIfMatch, idempotencyKey, request, v =>
        {
            var error = v.Complete(request.Outcome, request.Reason, At(request.CompletedAt));
            // zlecenie decyduje samo: zamyka się po wyniku „naprawione” z podpisem
            if (error is null && request.Outcome == VisitOutcomeType.Fixed) v.WorkOrder.Status = WorkOrderStatus.Closed;
            return error;
        }, ct);

    // ---------- komenda: idempotencja → wersja → reguła → zapis ----------

    private async Task<IActionResult> ExecuteAsync(Guid id, string? ifMatch, string? idempotencyKey, object? body, Func<Visit, ApiError?> command, CancellationToken ct)
    {
        var email = User.Email();
        var key = idempotencyKey?.Trim() ?? "";
        if (key.Length > 100) return this.Refuse(VisitErrors.Validation("Idempotency-Key: maks. 100 znaków."));
        var requestHash = Secrets.Sha256($"{Request.Method} {Request.Path}|{JsonSerializer.Serialize(body, json.Value.JsonSerializerOptions)}");

        if (key.Length > 0)
        {
            var expired = clock.Now - IdempotencyKeyLifetime;
            await db.IdempotencyRecords.Where(r => r.CreatedAt < expired).ExecuteDeleteAsync(ct);
            var saved = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.TechnicianEmail == email && r.Key == key, ct);
            if (saved is not null)
            {
                // powtórka: zwracamy zapisaną odpowiedź pierwszego żądania (nawet jeśli wizyta ma już nowszą wersję)
                if (saved.RequestHash != requestHash) return this.Refuse(IdempotencyErrors.KeyReused);
                if (saved.ETag is not null) Response.Headers.ETag = saved.ETag;
                Response.Headers["Idempotent-Replayed"] = "true";
                return new ContentResult { StatusCode = saved.StatusCode, Content = saved.ResponseJson, ContentType = "application/json; charset=utf-8" };
            }
        }

        var visit = await Load(id, tracking: true, ct);
        if (visit is null) return this.Refuse(VisitErrors.NotFound(id));

        // ifMatch = If-Match albo X-If-Match (proxy po drodze może wyciąć If-Match – ta sama wartość w X-If-Match)
        var expected = ETags.ParseVersion(ifMatch);
        if (expected is null) return this.Refuse(VisitErrors.PreconditionRequired);
        if (expected != visit.Version) return Conflict(expected.Value, visit);

        var error = command(visit);
        if (error is not null) return this.Refuse(error, Current(visit));

        visit.Version++;
        visit.UpdatedAt = clock.Now;
        var dto = await DetailsAsync(visit, ct);
        var etag = ETags.ForVersion(visit.Version);
        if (key.Length > 0)
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                TechnicianEmail = email, Key = key, RequestHash = requestHash, StatusCode = StatusCodes.Status200OK,
                ResponseJson = JsonSerializer.Serialize(dto, json.Value.JsonSerializerOptions), ETag = etag, CreatedAt = clock.Now
            });
        }

        try
        {
            await db.SaveChangesAsync(ct); // jedna transakcja: wizyta i klucz idempotencji
        }
        catch (DbUpdateConcurrencyException)
        {
            // ktoś (drugi telefon brygady, dyspozytor) zapisał wizytę między odczytem a zapisem
            db.ChangeTracker.Clear();
            var current = await Load(id, tracking: false, ct);
            return current is null ? this.Refuse(VisitErrors.NotFound(id)) : Conflict(expected.Value, current);
        }
        catch (DbUpdateException) when (key.Length > 0)
        {
            db.ChangeTracker.Clear();
            return this.Refuse(IdempotencyErrors.InProgress);
        }

        Response.Headers.ETag = etag;
        return Ok(dto);
    }

    private ObjectResult Conflict(int expected, Visit current)
    {
        Response.Headers.ETag = ETags.ForVersion(current.Version);
        return this.Refuse(VisitErrors.VersionConflict(expected, current.Version), Current(current));
    }

    private static Dictionary<string, object?> Current(Visit v) => new()
    {
        ["currentVersion"] = v.Version,
        ["currentStatus"] = v.Status.ToString()
    };

    private DateTimeOffset At(DateTimeOffset? clientTime) =>
        clientTime is { } t ? DemoClock.Microseconds(t.ToUniversalTime()) : clock.Now;

    private Task<Visit?> Load(Guid id, bool tracking, CancellationToken ct)
    {
        var email = User.Email();
        var query = db.Visits.Include(v => v.WorkOrder).Where(v => v.Id == id && v.TechnicianEmails.Contains(email));
        return (tracking ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    private async Task<VisitDetailsDto> DetailsAsync(Visit visit, CancellationToken ct)
    {
        var partIds = visit.Parts.Select(p => p.PartId).ToList();
        var partNames = await db.Parts.AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var technicianNames = await db.Technicians.AsNoTracking().Where(t => visit.TechnicianEmails.Contains(t.Email))
            .ToDictionaryAsync(t => t.Email, t => t.Name, ct);
        return visit.ToDetails(Request, partNames, technicianNames);
    }
}
