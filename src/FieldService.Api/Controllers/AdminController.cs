using System.Reflection;
using FieldService.Api.Auth;
using FieldService.Api.Contracts;
using FieldService.Api.Data;
using FieldService.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FieldService.Api.Controllers;

/// <summary>Administracja.</summary>
[ApiController]
[Route("api/admin")]
[AllowAnonymous]
[Produces("application/json")]
public sealed class AdminController(FieldServiceDbContext db, DemoSeeder seeder, IOptionsMonitor<AdminOptions> options) : ControllerBase
{
    /// <summary>Reset danych (wszystkich albo jednego technika). Wymaga nagłówka <c>X-Admin-Key</c>.</summary>
    /// <remarks>
    /// Jeden technik: jego wizyty (także wspólne wizyty brygady), zlecenia, auto, rezerwacje i sesje — przy następnym
    /// logowaniu powstaje nowy dzień pracy. Wszystko: cała baza, potem katalog i zapasy od nowa (z rezerwacją
    /// ostatniej sztuki `KOMPRESOR-R32`). Bez `Admin:Key` na serwerze reset jest wyłączony (403).
    /// </remarks>
    /// <param name="adminKey">Klucz administratora.</param>
    /// <param name="technicianEmail" example="jan.kowalski@firma.pl">Opcjonalnie: tylko ten technik.</param>
    /// <response code="200">Wyczyszczono.</response>
    /// <response code="401">Brak lub zły klucz.</response>
    /// <response code="403">Reset wyłączony.</response>
    [HttpPost("reset", Name = "ResetData")]
    [ProducesResponseType<ResetResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public ActionResult<ResetResult> Reset([FromHeader(Name = "X-Admin-Key")] string? adminKey, [FromQuery] string? technicianEmail)
    {
        var expected = options.CurrentValue.Key;
        if (string.IsNullOrEmpty(expected))
            return Problem(title: "Reset wyłączony", detail: "Na serwerze nie ustawiono Admin:Key.", statusCode: StatusCodes.Status403Forbidden);
        if (!Secrets.FixedTimeEquals(adminKey, expected))
            return Problem(title: "Brak dostępu", detail: "Nieprawidłowy lub brakujący nagłówek X-Admin-Key.", statusCode: StatusCodes.Status401Unauthorized);

        using var transaction = db.Database.BeginTransaction();
        if (string.IsNullOrWhiteSpace(technicianEmail))
        {
            var technicians = db.Technicians.Count();
            var visits = db.Visits.Count();
            db.WorkOrders.ExecuteDelete(); // kaskadowo wizyty
            db.Technicians.ExecuteDelete();
            db.VanStock.ExecuteDelete();
            db.RefreshTokens.ExecuteDelete();
            db.IdempotencyRecords.ExecuteDelete();
            db.PartStocks.ExecuteDelete();
            db.Parts.ExecuteDelete();
            db.Warehouses.ExecuteDelete();
            seeder.EnsureCatalog();
            transaction.Commit();
            return new ResetResult { RemovedTechnicians = technicians, RemovedVisits = visits, Message = "Usunięto wszystkie dane, katalog i zapasy od nowa." };
        }

        var key = Technicians.Normalize(technicianEmail);
        var workOrders = db.WorkOrders.Where(w => w.Visits.Any(v => v.TechnicianEmails.Contains(key)));
        var removedVisits = db.Visits.Count(v => v.TechnicianEmails.Contains(key));
        workOrders.ExecuteDelete();
        var removedTechnicians = db.Technicians.Where(t => t.Email == key).ExecuteDelete();
        db.VanStock.Where(v => v.TechnicianEmail == key).ExecuteDelete();
        db.RefreshTokens.Where(r => r.TechnicianEmail == key).ExecuteDelete();
        db.IdempotencyRecords.Where(r => r.TechnicianEmail == key).ExecuteDelete();
        foreach (var stock in db.PartStocks.ToList()) stock.Reservations.RemoveAll(r => r.TechnicianEmail == key);
        db.SaveChanges();
        transaction.Commit();
        return new ResetResult { RemovedTechnicians = removedTechnicians, RemovedVisits = removedVisits, Message = $"Usunięto dane technika {key}." };
    }
}

/// <summary>Stan usługi.</summary>
[ApiController]
[Route("api/health")]
[AllowAnonymous]
[Produces("application/json")]
public sealed class HealthController(
    FieldServiceDbContext db, IOptionsMonitor<AuthOptions> auth, IHostEnvironment env, IConfiguration configuration, TimeProvider time) : ControllerBase
{
    public static readonly string Version =
        typeof(HealthController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    /// <summary>Wersja, czas serwera, połączenie z bazą, PathBase i czy API eksportuje telemetrię.</summary>
    /// <response code="200">Usługa działa.</response>
    /// <response code="503">Brak połączenia z bazą danych.</response>
    [HttpGet(Name = "GetHealth")]
    [HttpHead(Name = "HeadHealth")] // monitory dostępności i `curl -I`
    [ProducesResponseType<HealthDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<HealthDto>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<HealthDto>> Get(CancellationToken ct)
    {
        bool databaseOk;
        try { databaseOk = await db.Database.CanConnectAsync(ct); }
        catch (Exception) { databaseOk = false; }

        var health = new HealthDto
        {
            Status = databaseOk ? "ok" : "error",
            DatabaseConnected = databaseOk,
            Version = Version,
            ServerTime = time.GetUtcNow(),
            PathBase = Request.PathBase.Value ?? "",
            AccessTokenLifetimeSeconds = auth.CurrentValue.AccessTokenLifetimeSeconds,
            TelemetryExport = Telemetry.ExportEnabled(configuration),
            Environment = env.EnvironmentName
        };
        return databaseOk ? health : StatusCode(StatusCodes.Status503ServiceUnavailable, health);
    }
}
