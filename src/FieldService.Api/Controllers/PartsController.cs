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

/// <summary>Katalog części, stany magazynowe i rezerwacje (model Magazyn).</summary>
[ApiController]
[Route("api")]
[Authorize]
[Produces("application/json")]
public sealed class PartsController(FieldServiceDbContext db, DemoClock clock, IOptionsMonitor<InventoryOptions> inventory) : ControllerBase
{
    /// <summary>Katalog części (nazwa, opis, cena, zdjęcie). Odpowiedź z <c>ETag</c> — <c>If-None-Match</c> → 304.</summary>
    [HttpGet("parts", Name = "GetParts")]
    [DemoDelay]
    [ProducesResponseType<IReadOnlyList<PartDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<IActionResult> Catalog(CancellationToken ct)
    {
        var parts = await db.Parts.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
        return this.OkWithETag(parts.Select(p => p.ToDto(Request)).ToList());
    }

    /// <summary>Magazyny.</summary>
    [HttpGet("warehouses", Name = "GetWarehouses")]
    [ProducesResponseType<IReadOnlyList<WarehouseDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<WarehouseDto>> Warehouses(CancellationToken ct) =>
        await db.Warehouses.AsNoTracking().OrderBy(w => w.Id).Select(w => new WarehouseDto { Id = w.Id, Name = w.Name }).ToListAsync(ct);

    /// <summary>Stan części w magazynach: na stanie, zarezerwowane (aktywne rezerwacje), dostępne.</summary>
    /// <response code="404">Nie ma takiej części.</response>
    [HttpGet("parts/{id}/stock", Name = "GetPartStock")]
    [ProducesResponseType<IReadOnlyList<StockLevelDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Stock(string id, CancellationToken ct)
    {
        var partId = id.Trim().ToUpperInvariant();
        if (!await db.Parts.AnyAsync(p => p.Id == partId, ct)) return this.Refuse(InventoryErrors.PartNotFound(partId));
        var warehouses = await db.Warehouses.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var stocks = await db.PartStocks.AsNoTracking().Where(s => s.PartId == partId).OrderBy(s => s.WarehouseId).ToListAsync(ct);
        var now = clock.Now;
        return Ok(stocks.Select(s => new StockLevelDto
        {
            PartId = s.PartId,
            WarehouseId = s.WarehouseId,
            WarehouseName = warehouses.GetValueOrDefault(s.WarehouseId) ?? s.WarehouseId,
            OnHand = s.OnHand,
            Reserved = s.Reserved(now),
            Available = s.Available(now)
        }).ToList());
    }

    /// <summary>Części w aucie technika.</summary>
    [HttpGet("vanstock", Name = "GetVanStock")]
    [ProducesResponseType<IReadOnlyList<VanStockItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> VanStock(CancellationToken ct)
    {
        var email = User.Email();
        var items = await db.VanStock.AsNoTracking().Where(v => v.TechnicianEmail == email)
            .Join(db.Parts, v => v.PartId, p => p.Id, (v, p) => new { v.PartId, p.Name, v.Quantity })
            .OrderBy(x => x.Name).ToListAsync(ct);
        return this.OkWithETag(items.Select(x => new VanStockItemDto
        {
            PartId = x.PartId, PartName = x.Name, Quantity = x.Quantity,
            ThumbnailUrl = Urls.Image(Request, Mappers.PartImageKey(x.PartId), thumbnail: true)
        }).ToList());
    }

    /// <summary>Rezerwacja części z magazynu dla wizyty (wygasa po <c>Inventory:ReservationMinutes</c>, domyślnie 30 min).</summary>
    /// <remarks>
    /// Reguła agregatu <c>PartStock</c> (jedna część w jednym magazynie): suma aktywnych rezerwacji ≤ stan.
    /// Brak sztuk → 409 <c>Inventory.OutOfStock</c> z polem <c>available</c>. Część <c>KOMPRESOR-R32</c> w MAG-01 ma
    /// jedną sztukę zarezerwowaną przez innego technika — rezerwacja zawsze kończy się konfliktem.
    /// </remarks>
    /// <response code="201">Zarezerwowano.</response>
    /// <response code="404">Brak części, magazynu albo wizyty technika.</response>
    /// <response code="409">Brak dostępnych sztuk.</response>
    /// <response code="422">Nieprawidłowa ilość.</response>
    [HttpPost("parts/reservations", Name = "ReservePart")]
    [ProducesResponseType<ReservationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reserve([FromBody] ReservePartRequest request, CancellationToken ct)
    {
        var email = User.Email();
        var partId = request.PartId?.Trim().ToUpperInvariant() ?? "";
        var warehouseId = request.WarehouseId?.Trim().ToUpperInvariant() ?? "";
        if (!await db.Parts.AnyAsync(p => p.Id == partId, ct)) return this.Refuse(InventoryErrors.PartNotFound(partId));
        if (!await db.Visits.AnyAsync(v => v.Id == request.VisitId && v.TechnicianEmails.Contains(email), ct))
            return this.Refuse(VisitErrors.NotFound(request.VisitId));

        // ostatnia sztuka: dwa równoczesne żądania — jedno zapisze, drugie dostanie konflikt wersji i sprawdzi regułę jeszcze raz
        for (var attempt = 0; ; attempt++)
        {
            var stock = await db.PartStocks.FirstOrDefaultAsync(s => s.PartId == partId && s.WarehouseId == warehouseId, ct);
            if (stock is null) return this.Refuse(InventoryErrors.StockNotFound(partId, warehouseId));

            var (reservation, error) = stock.Reserve(request.VisitId, email, request.Quantity, clock.Now,
                TimeSpan.FromMinutes(inventory.CurrentValue.ReservationMinutes));
            if (error is not null)
                return this.Refuse(error, new Dictionary<string, object?> { ["available"] = stock.Available(clock.Now) });
            try
            {
                await db.SaveChangesAsync(ct);
                return StatusCode(StatusCodes.Status201Created, reservation!.ToDto(stock));
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Aktywne rezerwacje technika.</summary>
    [HttpGet("parts/reservations", Name = "GetReservations")]
    [ProducesResponseType<IReadOnlyList<ReservationDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<ReservationDto>> Reservations(CancellationToken ct)
    {
        var email = User.Email();
        var now = clock.Now;
        var stocks = await db.PartStocks.AsNoTracking().ToListAsync(ct); // mała tabela: część × magazyn
        return stocks.SelectMany(s => s.Reservations.Where(r => r.TechnicianEmail == email && r.ExpiresAt > now).Select(r => r.ToDto(s)))
            .OrderBy(r => r.ExpiresAt).ToList();
    }

    /// <summary>Zwolnienie rezerwacji (sztuki wracają do puli).</summary>
    /// <response code="204">Zwolniono.</response>
    /// <response code="404">Brak rezerwacji technika.</response>
    [HttpDelete("parts/reservations/{id:guid}", Name = "ReleaseReservation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Release(Guid id, CancellationToken ct)
    {
        var email = User.Email();
        for (var attempt = 0; ; attempt++)
        {
            var stocks = await db.PartStocks.ToListAsync(ct); // mała tabela: część × magazyn
            var stock = stocks.FirstOrDefault(s => s.Reservations.Any(r => r.Id == id && r.TechnicianEmail == email));
            if (stock is null || !stock.Release(id, email)) return this.Refuse(InventoryErrors.ReservationNotFound(id));
            try
            {
                await db.SaveChangesAsync(ct);
                return NoContent();
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}

/// <summary>Zlecenia technika (model odczytu modelu Zlecenia).</summary>
[ApiController]
[Route("api/workorders")]
[Authorize]
[Produces("application/json")]
public sealed class WorkOrdersController(FieldServiceDbContext db, DemoClock clock) : ControllerBase
{
    /// <summary>„Moje zlecenia”: otwarte zlecenia z wizytami technika i zlecenia z wizytami na dziś.</summary>
    [HttpGet(Name = "GetMyWorkOrders")]
    [ProducesResponseType<IReadOnlyList<WorkOrderSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var email = User.Email();
        var (from, to) = clock.DayRange(clock.Today);
        var orders = await db.WorkOrders.AsNoTracking()
            .Where(w => w.Visits.Any(v => v.TechnicianEmails.Contains(email))
                        && (w.Status == WorkOrderStatus.Open || w.Visits.Any(v => v.PlannedStart >= from && v.PlannedStart < to)))
            .OrderBy(w => w.SlaDueAt)
            .Select(w => new WorkOrderSummaryDto
            {
                Id = w.Id,
                Number = w.Number,
                Status = w.Status,
                CustomerName = w.CustomerName,
                Address = w.Street + ", " + w.City,
                DeviceName = w.DeviceName,
                FaultDescription = w.FaultDescription,
                SlaDueAt = w.SlaDueAt,
                VisitCount = w.Visits.Count
            })
            .ToListAsync(ct);
        return this.OkWithETag(orders);
    }
}

/// <summary>Zdjęcia części i urządzeń (generowane, bez logowania — dla <c>UriImageSource</c>).</summary>
[ApiController]
[Route("api/images")]
[AllowAnonymous]
public sealed class ImagesController(PngImages images) : ControllerBase
{
    /// <summary>Obrazek PNG: pełny 800 px albo miniatura 160 px (<c>size=thumb</c>). Ten sam adres = ten sam obrazek.</summary>
    /// <param name="key">Klucz z <c>imageUrl</c>/<c>thumbnailUrl</c>.</param>
    /// <param name="size"><c>thumb</c> albo puste.</param>
    [HttpGet("{key:regex(^[[a-z0-9-]]{{1,64}}$)}", Name = "GetImage")]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK, "image/png")]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public IActionResult Get(string key, [FromQuery] string? size)
    {
        var thumbnail = size == "thumb";
        var etag = $"\"{key}-{(thumbnail ? PngImages.ThumbnailSize : PngImages.FullSize)}\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public, max-age=604800, immutable";
        if (Request.Headers.IfNoneMatch.ToString() == etag) return StatusCode(StatusCodes.Status304NotModified);
        return File(images.Get(key, thumbnail), "image/png");
    }
}
