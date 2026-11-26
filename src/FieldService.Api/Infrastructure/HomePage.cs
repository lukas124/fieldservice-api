using System.Net;
using FieldService.Api.Controllers;

namespace FieldService.Api.Infrastructure;

/// <summary>Prosta strona informacyjna pod <c>GET /</c>.</summary>
public static class HomePage
{
    public static string Render(string pathBase)
    {
        var b = WebUtility.HtmlEncode(pathBase);
        return $$"""
        <!doctype html>
        <html lang="pl">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>FieldService API — szkolenie .NET MAUI</title>
          <style>
            :root { color-scheme: light dark; --accent: #c43a0e; }
            body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif; max-width: 46rem; margin: 2rem auto; padding: 0 1rem; line-height: 1.5; }
            code { font-family: ui-monospace, "JetBrains Mono", Menlo, monospace; font-size: .9em; }
            a.button { display: inline-block; padding: .6rem 1rem; border-radius: .5rem; background: var(--accent); color: #fff; text-decoration: none; font-weight: 600; }
            table { border-collapse: collapse; width: 100%; font-size: .92rem; }
            td { padding: .3rem .5rem; border-bottom: 1px solid color-mix(in srgb, currentColor 15%, transparent); vertical-align: top; }
          </style>
        </head>
        <body>
          <h1>FieldService API</h1>
          <p>API szkoleniowe do aplikacji <strong>FieldService</strong> (szkolenie „Architektura aplikacji w .NET MAUI”). Wersja {{HealthController.Version}}.</p>
          <p><a class="button" href="{{b}}/swagger">Otwórz Swaggera</a> &nbsp; <a href="{{b}}/swagger/v1/swagger.json">swagger.json</a> &nbsp; <a href="{{b}}/api/health">/api/health</a></p>
          <h2>Jak zacząć</h2>
          <ol>
            <li><code>POST {{b}}/api/auth/login</code> z dowolnym poprawnym e-mailem i PIN-em <code>1234</code> – serwer tworzy dzień pracy (4 wizyty, jedna rozpoczęta).</li>
            <li>W Swaggerze: <em>Authorize</em> → wklej <code>accessToken</code>.</li>
            <li><code>GET {{b}}/api/visits/today</code> → wersja wizyty (<code>version</code>) do komend.</li>
            <li>Komenda wizyty: wersja w <code>X-If-Match</code> (proxy po drodze może wyciąć <code>If-Match</code>) i dowolny <code>Idempotency-Key</code> – wyślij dwa razy z tym samym kluczem.</li>
          </ol>
          <h2>Endpointy</h2>
          <table>
            <tr><td><code>POST /api/auth/login</code>, <code>POST /api/auth/refresh</code></td><td>logowanie, odświeżenie tokenu</td></tr>
            <tr><td><code>GET /api/visits/today</code>, <code>GET /api/visits/history</code></td><td>wizyty na dziś, historia (stronicowanie)</td></tr>
            <tr><td><code>GET /api/visits/{id}</code></td><td>szczegóły (ETag = wersja)</td></tr>
            <tr><td><code>POST /api/visits/{id}/start|activities|parts|complete</code>, <code>PUT …/signature</code></td><td>komendy wizyty (<code>X-If-Match</code>, <code>Idempotency-Key</code>)</td></tr>
            <tr><td><code>GET /api/parts</code>, <code>/api/warehouses</code>, <code>/api/vanstock</code></td><td>katalog, magazyny, auto technika</td></tr>
            <tr><td><code>POST /api/parts/reservations</code></td><td>rezerwacja części (ostatnia sztuka → 409)</td></tr>
            <tr><td><code>GET /api/workorders</code></td><td>moje zlecenia</td></tr>
            <tr><td><code>/api/dispatcher/…</code></td><td>dyspozytor (trener): anulowanie, przeniesienie, nowa wizyta, stan magazynu</td></tr>
          </table>
        </body>
        </html>
        """;
    }
}
