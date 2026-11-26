using FieldService.Api.Auth;
using FieldService.Api.Contracts;
using FieldService.Api.Data;
using FieldService.Api.Domain;
using FieldService.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FieldService.Api.Controllers;

/// <summary>Logowanie technika i odświeżanie tokenu.</summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
[EnableRateLimiting(RateLimiting.AuthPolicy)]
[Produces("application/json")]
public sealed class AuthController(
    TokenService tokens, FieldServiceDbContext db, DemoSeeder seeder, DemoClock clock, IOptionsMonitor<AuthOptions> options) : ControllerBase
{
    /// <summary>Logowanie: dowolny poprawny e-mail + PIN 1234.</summary>
    /// <remarks>
    /// Każdy e-mail to osobny technik z własnym dniem pracy — tworzonym przy pierwszym logowaniu danego dnia
    /// (4 wizyty na dziś, zlecenia, auto technika, historia). Konta `technik@szkolenie.pl` i `technik2@szkolenie.pl`
    /// to brygada (wspólna wizyta), `wydajnosc@szkolenie.pl` ma 2000 wizyt w historii.
    ///
    ///     POST /api/auth/login
    ///     { "email": "jan.kowalski@firma.pl", "pin": "1234" }
    /// </remarks>
    /// <response code="200">Zalogowano — tokeny i imię technika.</response>
    /// <response code="401">Nieprawidłowy e-mail lub PIN.</response>
    /// <response code="429">Za dużo prób z tego adresu IP.</response>
    [HttpPost("login", Name = "Login")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public ActionResult<TokenResponse> Login([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] LoginRequest? request)
    {
        var email = request?.Email?.Trim();
        if (!Technicians.IsValidEmail(email) || request?.Pin != options.CurrentValue.Pin /* dokładnie, bez przycinania */)
        {
            return Problem(title: "Nieprawidłowy e-mail lub PIN", detail: "Podaj poprawny adres e-mail i PIN 1234.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var technician = EnsureTechnician(email!);
        seeder.EnsureDemoDay(technician.Email);
        return tokens.Issue(technician.Email, technician.Name);
    }

    /// <summary>Odświeżenie tokenu: refresh token → nowa para tokenów.</summary>
    /// <remarks>Refresh token jest jednorazowy (rotacja): zapisz nowy z odpowiedzi. Wygasły, użyty lub nieznany → 401.</remarks>
    /// <response code="200">Nowe tokeny.</response>
    /// <response code="401">Refresh token nieprawidłowy lub wygasły.</response>
    [HttpPost("refresh", Name = "RefreshToken")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public ActionResult<TokenResponse> Refresh([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RefreshRequest? request)
    {
        var email = string.IsNullOrWhiteSpace(request?.RefreshToken) ? null : tokens.Consume(request.RefreshToken.Trim());
        var technician = email is null ? null : db.Technicians.Find(email);
        if (technician is null)
        {
            return Problem(title: "Sesja wygasła", detail: "Refresh token jest nieprawidłowy, wygasł lub został już użyty. Zaloguj się ponownie.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return tokens.Issue(technician.Email, technician.Name);
    }

    private Technician EnsureTechnician(string email)
    {
        var key = Technicians.Normalize(email);
        var technician = db.Technicians.Find(key);
        if (technician is null)
        {
            technician = new Technician { Email = key, Name = Technicians.NameFromEmail(key) };
            db.Technicians.Add(technician);
        }
        technician.LastSeenAt = clock.Now;
        db.SaveChanges();
        return technician;
    }
}
