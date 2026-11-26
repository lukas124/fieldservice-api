using System.Net;
using System.Text.Json;

namespace FieldService.Api.Tests.BlackBox;

/// <summary>
/// Listy i cache: ETag / If-None-Match → 304, historia stronicowana (maks. 100 na stronę),
/// obrazki PNG (pełny i miniatura, Cache-Control: immutable), health i dokument Swagger.
/// </summary>
public class ListsAndCacheBlackBoxTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    private static int PngWidth(byte[] png) => (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // ---------- ETag ----------

    [TestCase("api/visits/today")]
    [TestCase("api/parts")]
    [TestCase("api/workorders")]
    [TestCase("api/visits/history")]
    [TestCase("api/visits/history?page=1&pageSize=5")]
    public async Task Ten_sam_ETag_w_If_None_Match_daje_304_bez_tresci(string url)
    {
        var client = await Api.ClientAsync(_factory);
        var first = await client.GetAsync(url);
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var etag = first.Headers.ETag?.ToString() ?? throw new AssertionException($"{url} bez ETag");

        var second = await Bb.GetIfNoneMatchAsync(client, url, etag);

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NotModified), url);
        Assert.That(await second.Content.ReadAsByteArrayAsync(), Is.Empty);
    }

    [TestCase("api/visits/today")]
    [TestCase("api/parts")]
    public async Task Inny_ETag_w_If_None_Match_daje_200(string url)
    {
        var client = await Api.ClientAsync(_factory);

        var response = await Bb.GetIfNoneMatchAsync(client, url, "\"nie-ten-etag\"");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.ETag, Is.Not.Null);
    }

    [Test]
    public async Task Szczegoly_wizyty_z_aktualnym_ETag_daja_304()
    {
        var client = await Api.ClientAsync(_factory);
        var url = $"api/visits/{Bb.Id((await Bb.TodayAsync(client))[0])}";
        var etag = (await client.GetAsync(url)).Headers.ETag?.ToString() ?? throw new AssertionException("Szczegóły bez ETag");

        var response = await Bb.GetIfNoneMatchAsync(client, url, etag);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
    }

    [Test]
    public async Task Po_komendzie_lista_na_dzis_ma_nowy_ETag()
    {
        var client = await Api.ClientAsync(_factory);
        var etag = (await client.GetAsync("api/visits/today")).Headers.ETag?.ToString() ?? throw new AssertionException("Lista bez ETag");
        await Bb.StartPlannedAsync(client);

        var response = await Bb.GetIfNoneMatchAsync(client, "api/visits/today", etag);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.ETag?.ToString(), Is.Not.EqualTo(etag));
    }

    [Test]
    public async Task ETag_listy_jest_osobny_dla_kazdego_technika()
    {
        var a = await Api.ClientAsync(_factory);
        var b = await Api.ClientAsync(_factory);
        var etagA = (await a.GetAsync("api/visits/today")).Headers.ETag?.ToString() ?? throw new AssertionException("Lista bez ETag");

        // ETag technika A nie może dać 304 technikowi B (inne wizyty)
        var response = await Bb.GetIfNoneMatchAsync(b, "api/visits/today", etagA);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // ---------- Historia ----------

    [Test]
    public async Task Historia_domyslnie_strona_1_po_50_od_najnowszej()
    {
        var client = await Api.ClientAsync(_factory);

        var page = await Bb.GetOkAsync(client, "api/visits/history");

        Assert.That(Bb.Int(page, "page"), Is.EqualTo(1));
        Assert.That(Bb.Int(page, "pageSize"), Is.EqualTo(50));
        Assert.That(Bb.Int(page, "totalCount"), Is.EqualTo(20));
        var starts = Bb.Items(page.GetProperty("items")).Select(i => Bb.Time(i, "plannedStart")).ToList();
        Assert.That(starts, Has.Count.EqualTo(20));
        Assert.That(starts, Is.Ordered.Descending);
    }

    [Test]
    public async Task Historia_strony_sie_nie_nakladaja_i_skladaja_w_calosc()
    {
        var client = await Api.ClientAsync(_factory);
        var all = new List<Guid>();

        for (var p = 1; p <= 4; p++)
        {
            var page = await Bb.GetOkAsync(client, $"api/visits/history?page={p}&pageSize=5");
            Assert.That(Bb.Int(page, "page"), Is.EqualTo(p));
            Assert.That(Bb.Int(page, "pageSize"), Is.EqualTo(5));
            var items = Bb.Items(page.GetProperty("items"));
            Assert.That(items, Has.Count.EqualTo(5));
            all.AddRange(items.Select(Bb.Id));
        }

        Assert.That(all, Is.Unique);
        var full = Bb.Items((await Bb.GetOkAsync(client, "api/visits/history?pageSize=20")).GetProperty("items")).Select(Bb.Id);
        Assert.That(all, Is.EqualTo(full));
    }

    [Test]
    public async Task Historia_strona_poza_zakresem_jest_pusta()
    {
        var client = await Api.ClientAsync(_factory);

        var page = await Bb.GetOkAsync(client, "api/visits/history?page=5&pageSize=10");

        Assert.That(Bb.Items(page.GetProperty("items")), Is.Empty);
        Assert.That(Bb.Int(page, "totalCount"), Is.EqualTo(20));
    }

    [Test]
    public async Task Historia_nie_zawiera_wizyt_na_dzis()
    {
        var client = await Api.ClientAsync(_factory);
        var today = (await Bb.TodayAsync(client)).Select(Bb.Id);

        var history = Bb.Items((await Bb.GetOkAsync(client, "api/visits/history?pageSize=100")).GetProperty("items")).Select(Bb.Id);

        Assert.That(history.Intersect(today), Is.Empty);
    }

    [Test]
    public async Task Historia_pageSize_jest_ograniczony_do_100()
    {
        var client = await Api.ClientAsync(_factory, Bb.PerformanceEmail);

        var first = await Bb.GetOkAsync(client, "api/visits/history?page=1&pageSize=500");

        Assert.That(Bb.Int(first, "pageSize"), Is.EqualTo(100));
        Assert.That(Bb.Items(first.GetProperty("items")), Has.Count.EqualTo(100));
        Assert.That(Bb.Int(first, "totalCount"), Is.EqualTo(120));
        var second = await Bb.GetOkAsync(client, "api/visits/history?page=2&pageSize=100");
        Assert.That(Bb.Items(second.GetProperty("items")), Has.Count.EqualTo(20));
        var firstIds = Bb.Items(first.GetProperty("items")).Select(Bb.Id);
        var secondIds = Bb.Items(second.GetProperty("items")).Select(Bb.Id);
        Assert.That(firstIds.Intersect(secondIds), Is.Empty);
    }

    [Test]
    public async Task Pozycja_historii_ma_dane_do_listy()
    {
        var client = await Api.ClientAsync(_factory);

        var items = Bb.Items((await Bb.GetOkAsync(client, "api/visits/history")).GetProperty("items"));

        foreach (var item in items)
        {
            foreach (var field in new[] { "workOrderNumber", "customerName", "city", "deviceName", "thumbnailUrl" })
                Assert.That(Bb.Str(item, field), Is.Not.Null.And.Not.Empty, field);
            if (Bb.Status(item) == "Completed")
                Assert.That(Bb.Str(item, "outcome"), Is.Not.Null, "Zakończona wizyta bez wyniku");
        }
    }

    // ---------- Obrazki ----------

    [Test]
    public async Task Obrazek_pelny_800_px_i_miniatura_160_px_bez_logowania()
    {
        var client = _factory.CreateClient();

        var full = await client.GetAsync("api/images/pompa-ciepla-1");
        var thumb = await client.GetAsync("api/images/pompa-ciepla-1?size=thumb");

        foreach (var response in new[] { full, thumb })
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/png"));
            Assert.That(Bb.Header(response, "Cache-Control"), Does.Contain("immutable"));
        }
        var fullBytes = await full.Content.ReadAsByteArrayAsync();
        var thumbBytes = await thumb.Content.ReadAsByteArrayAsync();
        Assert.That(fullBytes.Take(8), Is.EqualTo(PngSignature));
        Assert.That(thumbBytes.Take(8), Is.EqualTo(PngSignature));
        Assert.That(PngWidth(fullBytes), Is.EqualTo(800));
        Assert.That(PngWidth(thumbBytes), Is.EqualTo(160));
        Assert.That(thumbBytes.Length, Is.LessThan(fullBytes.Length));
    }

    [Test]
    public async Task Ten_sam_adres_obrazka_to_ten_sam_obrazek()
    {
        var client = _factory.CreateClient();

        var first = await client.GetByteArrayAsync("api/images/klimatyzator-7?size=thumb");
        var second = await client.GetByteArrayAsync("api/images/klimatyzator-7?size=thumb");

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public async Task Zdjecia_z_katalogu_czesci_prowadza_do_obrazkow_PNG()
    {
        var client = await Api.ClientAsync(_factory);
        var part = Bb.Items(await Bb.GetOkAsync(client, "api/parts"))[0];
        var anonymous = _factory.CreateClient();

        foreach (var field in new[] { "imageUrl", "thumbnailUrl" })
        {
            var url = Bb.Str(part, field)!;
            var index = url.IndexOf("api/images/", StringComparison.Ordinal);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), $"{field}: {url}");
            var response = await anonymous.GetAsync(url[index..]);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/png"));
        }
    }

    // ---------- Health i Swagger ----------

    [Test]
    public async Task Health_pokazuje_polaczenie_z_baza_i_czas_serwera()
    {
        var client = _factory.CreateClient();

        var health = await Bb.GetOkAsync(client, "api/health");

        Assert.That(health.GetProperty("databaseConnected").GetBoolean(), Is.True);
        Assert.That(Bb.Str(health, "status"), Is.Not.Null.And.Not.Empty);
        Assert.That(Bb.Str(health, "version"), Is.Not.Null.And.Not.Empty);
        Assert.That(Bb.Time(health, "serverTime"), Is.EqualTo(DateTimeOffset.UtcNow).Within(TimeSpan.FromMinutes(1)));
        Assert.That(Bb.Int(health, "accessTokenLifetimeSeconds"), Is.GreaterThan(0));
        Assert.That(health.GetProperty("telemetryExport").ValueKind, Is.EqualTo(JsonValueKind.False).Or.EqualTo(JsonValueKind.True));
        Assert.That(Bb.Str(health, "environment"), Is.EqualTo("Testing"));
    }

    [Test]
    public async Task Health_odpowiada_na_HEAD()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "api/health"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Czas_zycia_tokenu_z_health_zgadza_sie_z_logowaniem()
    {
        var client = _factory.CreateClient();
        var health = await Bb.GetOkAsync(client, "api/health");

        var tokens = await Bb.LoginJsonAsync(client, Api.NewEmail());

        Assert.That(Bb.Int(tokens, "expiresIn"), Is.EqualTo(Bb.Int(health, "accessTokenLifetimeSeconds")));
    }

    [Test]
    public async Task Swagger_ma_servers_i_endpointy_z_kontraktu()
    {
        var client = _factory.CreateClient();

        var doc = await Bb.GetOkAsync(client, "swagger/v1/swagger.json");

        var servers = Bb.Items(doc.GetProperty("servers"));
        Assert.That(servers, Is.Not.Empty);
        Assert.That(Bb.Str(servers[0], "url"), Is.EqualTo("/"), "Bez PathBase serwer to \"/\"");
        var paths = doc.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.That(paths, Is.SupersetOf(new[]
        {
            "/api/auth/login", "/api/auth/refresh", "/api/visits/today", "/api/visits/history", "/api/visits/{id}",
            "/api/visits/{id}/start", "/api/visits/{id}/complete", "/api/parts/reservations", "/api/admin/reset", "/api/health"
        }));
        Assert.That(doc.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _), Is.True);
    }

    [Test]
    public async Task Swagger_ma_czytelne_operationId_opisy_grup_i_naglowki_komend()
    {
        var client = _factory.CreateClient();

        var doc = await Bb.GetOkAsync(client, "swagger/v1/swagger.json");

        var paths = doc.GetProperty("paths");
        string OperationId(string path, string method) => Bb.Str(paths.GetProperty(path).GetProperty(method), "operationId")!;
        Assert.Multiple(() =>
        {
            Assert.That(OperationId("/api/visits/today", "get"), Is.EqualTo("GetTodayVisits"));
            Assert.That(OperationId("/api/parts/reservations", "post"), Is.EqualTo("ReservePart"));
            Assert.That(OperationId("/api/visits/{id}/complete", "post"), Is.EqualTo("CompleteVisit"));
            Assert.That(OperationId("/api/auth/refresh", "post"), Is.EqualTo("RefreshToken"));
        });

        var headers = Bb.Items(paths.GetProperty("/api/visits/{id}/start").GetProperty("post").GetProperty("parameters"))
            .Where(p => Bb.Str(p, "in") == "header").Select(p => Bb.Str(p, "name")).ToList();
        Assert.That(headers, Is.SupersetOf(new[] { "If-Match", "X-If-Match", "Idempotency-Key" }));

        var dispatcherHeaders = Bb.Items(paths.GetProperty("/api/dispatcher/visits/{id}/cancel").GetProperty("post").GetProperty("parameters"))
            .Select(p => Bb.Str(p, "name")).ToList();
        Assert.That(dispatcherHeaders, Does.Contain("X-Dispatcher-Key"));

        var tags = Bb.Items(doc.GetProperty("tags")).Select(t => Bb.Str(t, "name")).ToList();
        Assert.That(tags, Is.SupersetOf(new[] { "Auth", "Visits", "Parts", "WorkOrders", "Dispatcher" }));
        var visitSummary = doc.GetProperty("components").GetProperty("schemas").GetProperty("VisitSummaryDto").GetProperty("properties");
        Assert.That(visitSummary.TryGetProperty("estimatedArrival", out _), Is.True);
    }

    [Test]
    public async Task Strona_startowa_prowadzi_do_Swaggera()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
        var html = await response.Content.ReadAsStringAsync();
        Assert.That(html, Does.Contain("/swagger").And.Contain("Authorize").And.Contain("X-If-Match"));
    }
}
