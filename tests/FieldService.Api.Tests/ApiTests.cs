using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FieldService.Api.Contracts;
using FieldService.Api.Domain;

namespace FieldService.Api.Tests;

public sealed class AuthTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Login_returns_tokens_and_creates_demo_day()
    {
        var client = _factory.CreateClient();

        var tokens = await Api.LoginAsync(client, "jan.kowalski@firma.pl");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var today = await Api.TodayAsync(client);

        Assert.That(tokens.TechnicianName, Is.EqualTo("Jan Kowalski"));
        Assert.That(today, Has.Count.EqualTo(4));
        Assert.That(today.Count(v => v.Status == VisitStatus.InProgress), Is.EqualTo(1));
    }

    [Test]
    public async Task Second_login_does_not_duplicate_day()
    {
        var email = Api.NewEmail();
        await Api.ClientAsync(_factory, email);

        var client = await Api.ClientAsync(_factory, email);

        Assert.That(await Api.TodayAsync(client), Has.Count.EqualTo(4));
    }

    [TestCase("jan.kowalski@firma.pl", "0000")]
    [TestCase("to-nie-jest-email", "1234")]
    public async Task Login_wrong_pin_or_email_returns_401(string email, string pin)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("api/auth/login", new { email, pin });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Refresh_rotates_token_and_old_one_is_rejected()
    {
        var client = _factory.CreateClient();
        var first = await Api.LoginAsync(client, Api.NewEmail());

        var refreshed = await client.PostAsJsonAsync("api/auth/refresh", new { refreshToken = first.RefreshToken });
        var reused = await client.PostAsJsonAsync("api/auth/refresh", new { refreshToken = first.RefreshToken });

        Assert.That(refreshed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(reused.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Visits_without_token_return_401()
    {
        var response = await _factory.CreateClient().GetAsync("api/visits/today");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}

public sealed class ReadModelTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Today_with_same_ETag_returns_304()
    {
        var client = await Api.ClientAsync(_factory);
        var first = await client.GetAsync("api/visits/today");

        var request = new HttpRequestMessage(HttpMethod.Get, "api/visits/today");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var second = await client.SendAsync(request);

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
    }

    [Test]
    public async Task Catalog_has_stable_ETag_and_image_urls()
    {
        var client = await Api.ClientAsync(_factory);

        var a = await client.GetAsync("api/parts");
        var b = await client.GetAsync("api/parts");
        var parts = await Api.ReadAsync<List<PartDto>>(a);

        Assert.That(a.Headers.ETag, Is.EqualTo(b.Headers.ETag));
        Assert.That(parts, Has.Count.EqualTo(12));
        Assert.That(parts[0].ThumbnailUrl, Does.Contain("/api/images/part-").And.EndsWith("?size=thumb"));
    }

    [Test]
    public async Task Brigade_accounts_share_one_visit_today()
    {
        var technik = await Api.ClientAsync(_factory, "technik@szkolenie.pl");
        var technik2 = await Api.ClientAsync(_factory, "technik2@szkolenie.pl");

        var a = (await Api.TodayAsync(technik)).Single(v => v.IsBrigade);
        var b = (await Api.TodayAsync(technik2)).Single(v => v.IsBrigade);

        Assert.That(a.Id, Is.EqualTo(b.Id));
        var details = (await technik.GetFromJsonAsync<VisitDetailsDto>($"api/visits/{a.Id}", Api.Json))!;
        Assert.That(details.TechnicianEmails, Is.EqualTo(new[] { "technik@szkolenie.pl", "technik2@szkolenie.pl" }));
        Assert.That(await Api.TodayAsync(technik2), Has.Count.EqualTo(4));
    }

    [Test]
    public async Task History_is_paged_and_performance_account_has_large_history()
    {
        var client = await Api.ClientAsync(_factory, "wydajnosc@szkolenie.pl");

        var page = (await client.GetFromJsonAsync<PageDto<VisitHistoryItemDto>>("api/visits/history?page=2&pageSize=50", Api.Json))!;

        Assert.That(page.TotalCount, Is.EqualTo(120)); // Demo:PerformanceHistoryVisits w ApiFactory
        Assert.That(page.Items, Has.Count.EqualTo(50));
        Assert.That(page.Items.Select(i => i.ThumbnailUrl).Distinct().Count(), Is.EqualTo(50));
    }

    [Test]
    public async Task Image_is_png_and_thumbnail_is_smaller()
    {
        var client = _factory.CreateClient();

        var full = await client.GetByteArrayAsync("api/images/part-zawor-4d");
        var thumb = await client.GetByteArrayAsync("api/images/part-zawor-4d?size=thumb");

        Assert.That(full[..4], Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        Assert.That(thumb.Length, Is.LessThan(full.Length / 5));
    }

    [Test]
    public async Task Van_stock_and_work_orders_are_seeded()
    {
        var client = await Api.ClientAsync(_factory);

        var van = (await client.GetFromJsonAsync<List<VanStockItemDto>>("api/vanstock", Api.Json))!;
        var orders = (await client.GetFromJsonAsync<List<WorkOrderSummaryDto>>("api/workorders", Api.Json))!;

        Assert.That(van, Has.Count.EqualTo(6));
        Assert.That(orders, Has.Count.GreaterThanOrEqualTo(4));
    }
}

public sealed class InventoryTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    private static async Task<HttpResponseMessage> ReserveAsync(HttpClient client, string partId, int quantity = 1)
    {
        var visit = (await Api.TodayAsync(client))[0];
        return await client.PostAsJsonAsync("api/parts/reservations",
            new ReservePartRequest { PartId = partId, WarehouseId = "MAG-01", VisitId = visit.Id, Quantity = quantity }, Api.Json);
    }

    [Test]
    public async Task Last_compressor_is_reserved_by_someone_else()
    {
        var client = await Api.ClientAsync(_factory);

        var response = await ReserveAsync(client, "KOMPRESOR-R32");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await Api.ErrorCodeAsync(response), Is.EqualTo("Inventory.OutOfStock"));
    }

    [Test]
    public async Task Reservation_lowers_availability_and_release_restores_it()
    {
        var client = await Api.ClientAsync(_factory);
        async Task<int> AvailableAsync() =>
            (await client.GetFromJsonAsync<List<StockLevelDto>>("api/parts/ZAWOR-4D/stock", Api.Json))!.Single(s => s.WarehouseId == "MAG-01").Available;
        var before = await AvailableAsync();

        var reservation = await Api.ReadAsync<ReservationDto>(await ReserveAsync(client, "ZAWOR-4D", 2));
        var during = await AvailableAsync();
        var released = await client.DeleteAsync($"api/parts/reservations/{reservation.Id}");

        Assert.That(during, Is.EqualTo(before - 2));
        Assert.That(released.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await AvailableAsync(), Is.EqualTo(before));
    }

    [Test]
    public async Task Parallel_reservations_of_last_piece_succeed_exactly_once()
    {
        var dispatcher = _factory.CreateClient();
        await dispatcher.PutAsJsonAsync("api/dispatcher/stock/PRESOSTAT-HP/MAG-01", new SetStockRequest { OnHand = 1 });
        var clients = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Api.ClientAsync(_factory)));

        var responses = await Task.WhenAll(clients.Select(c => ReserveAsync(c, "PRESOSTAT-HP")));

        Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Created), Is.EqualTo(1));
        Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Conflict), Is.EqualTo(4));
    }

    [Test]
    public async Task Reservation_for_visit_of_other_technician_returns_404()
    {
        var owner = await Api.ClientAsync(_factory);
        var visit = (await Api.TodayAsync(owner))[0];
        var other = await Api.ClientAsync(_factory);

        var response = await other.PostAsJsonAsync("api/parts/reservations",
            new ReservePartRequest { PartId = "ZAWOR-4D", WarehouseId = "MAG-01", VisitId = visit.Id }, Api.Json);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}

public sealed class AdminTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Reset_technician_removes_day_and_next_login_creates_new_one()
    {
        var email = Api.NewEmail();
        var before = await Api.TodayAsync(await Api.ClientAsync(_factory, email));
        var admin = _factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, $"api/admin/reset?technicianEmail={email}");
        request.Headers.Add("X-Admin-Key", ApiFactory.AdminKey);
        var reset = await admin.SendAsync(request);
        var after = await Api.TodayAsync(await Api.ClientAsync(_factory, email));

        Assert.That(reset.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(after.Select(v => v.Id), Is.Not.EquivalentTo(before.Select(v => v.Id)));
    }

    [Test]
    public async Task Reset_without_key_returns_401()
    {
        var response = await _factory.CreateClient().PostAsync("api/admin/reset", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Health_reports_database()
    {
        var health = await _factory.CreateClient().GetFromJsonAsync<HealthDto>("api/health", Api.Json);

        Assert.That(health!.DatabaseConnected, Is.True);
    }
}
