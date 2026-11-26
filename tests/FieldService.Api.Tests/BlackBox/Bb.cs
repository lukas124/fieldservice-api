using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FieldService.Api.Tests.BlackBox;

/// <summary>
/// Pomocniki testów czarnej skrzynki. Wszystko idzie przez HTTP, odpowiedzi czytamy jako JSON (JsonElement),
/// żeby testy zależały tylko od kontraktu (README + openapi.json), a nie od typów z kodu API.
/// </summary>
internal static class Bb
{
    /// <summary>Strefa dnia pracy (Demo:TimeZone) – „dziś” liczy się w tej strefie.</summary>
    public static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    public const string BrigadeEmail1 = "technik@szkolenie.pl";
    public const string BrigadeEmail2 = "technik2@szkolenie.pl";
    public const string PerformanceEmail = "wydajnosc@szkolenie.pl";
    public const string LastPieceSku = "KOMPRESOR-R32";
    public const string MainWarehouse = "MAG-01";

    // ---------- JSON i ProblemDetails ----------

    public static async Task<JsonElement> ParseAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>Sprawdza status i zwraca treść jako JSON.</summary>
    public static async Task<JsonElement> ExpectAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(status), $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    public static Task<JsonElement> OkAsync(HttpResponseMessage response) => ExpectAsync(response, HttpStatusCode.OK);

    public static async Task<JsonElement> GetOkAsync(HttpClient client, string url) => await OkAsync(await client.GetAsync(url));

    /// <summary>Sprawdza status i pole <c>code</c> z ProblemDetails; zwraca całe ProblemDetails.</summary>
    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var problem = await ExpectAsync(response, status);
        Assert.That(Str(problem, "code"), Is.EqualTo(code), problem.ToString());
        return problem;
    }

    public static List<JsonElement> Items(JsonElement array) => array.EnumerateArray().ToList();

    public static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    public static int Version(JsonElement e) => e.GetProperty("version").GetInt32();

    public static string? Status(JsonElement e) => e.GetProperty("status").GetString();

    public static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    public static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static DateTimeOffset Time(JsonElement e, string name) => e.GetProperty(name).GetDateTimeOffset();

    public static bool IsNullOrMissing(JsonElement e, string name) =>
        !e.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    /// <summary>Podpis z DTO wizyty albo null, gdy wizyta nie jest podpisana.</summary>
    public static JsonElement? Signature(JsonElement visit) =>
        visit.TryGetProperty("signature", out var s) && s.ValueKind == JsonValueKind.Object ? s : null;

    /// <summary>Podpis, który musi być w DTO wizyty.</summary>
    public static JsonElement RequiredSignature(JsonElement visit) =>
        Signature(visit) ?? throw new AssertionException($"Wizyta bez podpisu: {visit}");

    /// <summary>Nagłówek odpowiedzi (także nagłówki treści) albo null.</summary>
    public static string? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values)) return string.Join(",", values);
        if (response.Content.Headers.TryGetValues(name, out var contentValues)) return string.Join(",", contentValues);
        return null;
    }

    // ---------- Czas ----------

    public static DateTimeOffset ToWarsaw(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, Warsaw);

    public static DateTime WarsawToday => ToWarsaw(DateTimeOffset.UtcNow).Date;

    /// <summary>Dzisiejsza data w strefie dnia pracy o podanej godzinie, jako czas UTC.</summary>
    public static DateTimeOffset WarsawTodayAt(int hour, int minute)
    {
        var local = WarsawToday.AddHours(hour).AddMinutes(minute);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Warsaw.GetUtcOffset(local)).ToUniversalTime();
    }

    // ---------- Logowanie ----------

    public static async Task<JsonElement> LoginJsonAsync(HttpClient client, string email, string pin = "1234") =>
        await OkAsync(await client.PostAsJsonAsync("api/auth/login", new { email, pin }));

    public static void UseToken(HttpClient client, string? accessToken) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    // ---------- Wizyty ----------

    public static async Task<List<JsonElement>> TodayAsync(HttpClient client) => Items(await GetOkAsync(client, "api/visits/today"));

    public static Task<JsonElement> VisitAsync(HttpClient client, Guid id) => GetOkAsync(client, $"api/visits/{id}");

    /// <summary>Pierwsza wizyta na dziś o danym statusie (bez wizyt brygady).</summary>
    public static async Task<JsonElement> FirstWithStatusAsync(HttpClient client, string status)
    {
        var visit = (await TodayAsync(client))
            .FirstOrDefault(v => Status(v) == status && !v.GetProperty("isBrigade").GetBoolean());
        Assert.That(visit.ValueKind, Is.EqualTo(JsonValueKind.Object), $"Brak wizyty na dziś o statusie {status}");
        return visit;
    }

    /// <summary>Komenda wizyty, która ma się udać (200); zwraca szczegóły wizyty po komendzie.</summary>
    public static async Task<JsonElement> OkCommandAsync(HttpClient client, HttpMethod method, string url, object? body, int version) =>
        await OkAsync(await Api.CommandAsync(client, method, url, body, version));

    /// <summary>Rozpoczyna pierwszą zaplanowaną wizytę – wynik: wizyta w trakcie z pustym zakresem prac.</summary>
    public static async Task<JsonElement> StartPlannedAsync(HttpClient client)
    {
        var planned = await FirstWithStatusAsync(client, "Planned");
        return await OkCommandAsync(client, HttpMethod.Post, $"api/visits/{Id(planned)}/start", new { }, Version(planned));
    }

    public static Task<JsonElement> RecordActivityAsync(HttpClient client, JsonElement visit, string description = "Diagnostyka układu") =>
        OkCommandAsync(client, HttpMethod.Post, $"api/visits/{Id(visit)}/activities", new { code = "DIAG", description }, Version(visit));

    public static Task<JsonElement> UsePartAsync(HttpClient client, JsonElement visit, string partId, int quantity = 1) =>
        OkCommandAsync(client, HttpMethod.Post, $"api/visits/{Id(visit)}/parts", new { partId, quantity }, Version(visit));

    public static Task<JsonElement> SignAsync(HttpClient client, JsonElement visit, string signerName = "Anna Wiśniewska") =>
        OkCommandAsync(client, HttpMethod.Put, $"api/visits/{Id(visit)}/signature", new { signerName }, Version(visit));

    public static Task<HttpResponseMessage> CompleteAsync(HttpClient client, JsonElement visit, string outcome, string? reason = null) =>
        Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{Id(visit)}/complete", new { outcome, reason }, Version(visit));

    /// <summary>Opis komendy wizyty do testów parametryzowanych (te same reguły nagłówków dla każdej komendy).</summary>
    public static (HttpMethod Method, string Url, object? Body) Command(string name, Guid visitId, string partId) => name switch
    {
        "start" => (HttpMethod.Post, $"api/visits/{visitId}/start", (object?)new { }),
        "activities" => (HttpMethod.Post, $"api/visits/{visitId}/activities", (object?)new { code = "DIAG", description = "Diagnostyka" }),
        "parts" => (HttpMethod.Post, $"api/visits/{visitId}/parts", (object?)new { partId, quantity = 1 }),
        "remove-part" => (HttpMethod.Delete, $"api/visits/{visitId}/parts/{partId}", null),
        "signature" => (HttpMethod.Put, $"api/visits/{visitId}/signature", (object?)new { signerName = "Anna Wiśniewska" }),
        "complete" => (HttpMethod.Post, $"api/visits/{visitId}/complete", (object?)new { outcome = "NotFixed", reason = "Klient nie zgodził się na naprawę" }),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Nieznana komenda")
    };

    // ---------- Magazyn ----------

    public static async Task<List<string>> PartIdsAsync(HttpClient client) =>
        Items(await GetOkAsync(client, "api/parts")).Select(p => p.GetProperty("id").GetString()!).ToList();

    /// <summary>Pierwsza część z katalogu inna niż KOMPRESOR-R32 (do zużycia na wizycie).</summary>
    public static async Task<string> AnyPartIdAsync(HttpClient client, int index = 0) =>
        (await PartIdsAsync(client)).Where(p => p != LastPieceSku).ElementAt(index);

    public static async Task<JsonElement> StockAsync(HttpClient client, string partId, string warehouseId)
    {
        var level = Items(await GetOkAsync(client, $"api/parts/{partId}/stock"))
            .FirstOrDefault(l => Str(l, "warehouseId") == warehouseId);
        Assert.That(level.ValueKind, Is.EqualTo(JsonValueKind.Object), $"Brak stanu {partId} w {warehouseId}");
        return level;
    }

    /// <summary>Wszystkie pary (część, magazyn) ze stanem, bez KOMPRESOR-R32 – stała kolejność z katalogu.</summary>
    public static async Task<List<(string PartId, string WarehouseId)>> StockPairsAsync(HttpClient client)
    {
        var pairs = new List<(string PartId, string WarehouseId)>();
        foreach (var partId in await PartIdsAsync(client))
        {
            if (partId == LastPieceSku) continue;
            foreach (var level in Items(await GetOkAsync(client, $"api/parts/{partId}/stock")))
                pairs.Add((partId, Str(level, "warehouseId")!));
        }
        Assert.That(pairs, Is.Not.Empty, "Brak stanów magazynowych");
        return pairs;
    }

    /// <summary>Ustawia stan przez dyspozytora tak, żeby dostępnych było dokładnie <paramref name="available"/> sztuk.</summary>
    public static async Task<JsonElement> SetAvailableAsync(HttpClient client, string partId, string warehouseId, int available)
    {
        var level = await StockAsync(client, partId, warehouseId);
        var onHand = Int(level, "reserved") + available;
        var updated = await OkAsync(await client.PutAsJsonAsync($"api/dispatcher/stock/{partId}/{warehouseId}", new { onHand }));
        Assert.That(Int(updated, "available"), Is.EqualTo(available), updated.ToString());
        return updated;
    }

    public static Task<HttpResponseMessage> ReserveAsync(HttpClient client, string partId, string warehouseId, Guid visitId, int quantity = 1) =>
        client.PostAsJsonAsync("api/parts/reservations", new { partId, warehouseId, visitId, quantity });

    public static async Task<List<JsonElement>> ReservationsAsync(HttpClient client) =>
        Items(await GetOkAsync(client, "api/parts/reservations"));

    // ---------- Cache ----------

    public static Task<HttpResponseMessage> GetIfNoneMatchAsync(HttpClient client, string url, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        return client.SendAsync(request);
    }

    // ---------- Admin ----------

    public static Task<HttpResponseMessage> AdminResetAsync(HttpClient client, string? key, string? technicianEmail = null)
    {
        var url = technicianEmail is null ? "api/admin/reset" : $"api/admin/reset?technicianEmail={Uri.EscapeDataString(technicianEmail)}";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (key is not null) request.Headers.Add("X-Admin-Key", key);
        return client.SendAsync(request);
    }
}
