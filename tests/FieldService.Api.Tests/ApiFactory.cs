using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldService.Api.Contracts;
using FieldService.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace FieldService.Api.Tests;

/// <summary>
/// API w pamięci (TestServer) z własną bazą PostgreSQL (tworzoną przez API przy starcie, usuwaną w Dispose)
/// i tymczasowym katalogiem danych. Limity żądań wyłączone.
/// Serwer bazy: zmienna FIELDSERVICE_TEST_DB (domyślnie lokalny Postgres, użytkownik fieldservice/fieldservice).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string AdminKey = "test-admin-key";

    private readonly Dictionary<string, string?> _settings;

    public static readonly string TestServer =
        Environment.GetEnvironmentVariable("FIELDSERVICE_TEST_DB") ?? "Host=localhost;Username=fieldservice;Password=fieldservice";

    public ApiFactory(Dictionary<string, string?>? settings = null)
    {
        _settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:FieldService"] = $"{TestServer};Database=fieldservice_test_{Guid.NewGuid():N}",
            ["Storage:DataDirectory"] = Path.Combine(Path.GetTempPath(), "fieldservice-api-tests", Guid.NewGuid().ToString("N")),
            ["Admin:Key"] = AdminKey,
            ["RateLimiting:Enabled"] = "false",
            ["Auth:SigningKey"] = "test-signing-key-test-signing-key-0123456789",
            ["Demo:PerformanceHistoryVisits"] = "120"
        };
        foreach (var (k, v) in settings ?? []) _settings[k] = v;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (k, v) in _settings) builder.UseSetting(k, v);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        var options = new DbContextOptionsBuilder<FieldServiceDbContext>().UseNpgsql(_settings["ConnectionStrings:FieldService"]).Options;
        using (var db = new FieldServiceDbContext(options)) db.Database.EnsureDeleted();
        try { Directory.Delete(_settings["Storage:DataDirectory"]!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class Api
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static string NewEmail(string prefix = "technik") => $"{prefix}.{Guid.NewGuid():N}@firma.pl";

    public static async Task<TokenResponse> LoginAsync(HttpClient client, string email, string pin = "1234")
    {
        var response = await client.PostAsJsonAsync("api/auth/login", new { email, pin });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
    }

    /// <summary>Loguje technika (nowego, jeśli bez e-maila) i zwraca klienta z nagłówkiem Bearer.</summary>
    public static async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> factory, string? email = null)
    {
        var client = factory.CreateClient();
        var tokens = await LoginAsync(client, email ?? NewEmail());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(Json))!;

    public static async Task<List<VisitSummaryDto>> TodayAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<VisitSummaryDto>>("api/visits/today", Json))!;

    /// <summary>Komenda wizyty z If-Match (null = bez nagłówka) i opcjonalnym Idempotency-Key.</summary>
    public static Task<HttpResponseMessage> CommandAsync(HttpClient client, HttpMethod method, string url, object? body, int? version, string? key = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    /// <summary>Kod błędu z ProblemDetails (pole <c>code</c>).</summary>
    public static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
