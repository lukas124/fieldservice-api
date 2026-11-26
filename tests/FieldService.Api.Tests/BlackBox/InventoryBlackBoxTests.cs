using System.Net;

namespace FieldService.Api.Tests.BlackBox;

/// <summary>
/// Magazyn części: katalog, magazyny, stany i rezerwacje (agregat PartStock – suma aktywnych rezerwacji ≤ stan).
/// Testy, które rezerwują, używają różnych par (część, magazyn) i same ustawiają dostępność przez dyspozytora.
/// </summary>
public class InventoryBlackBoxTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    /// <summary>Technik, jego wizyta i para (część, magazyn) z ustawioną dostępnością.</summary>
    private async Task<(HttpClient Client, Guid VisitId, string PartId, string WarehouseId)> PrepareAsync(int pairIndex, int available)
    {
        var client = await Api.ClientAsync(_factory);
        var (partId, warehouseId) = (await Bb.StockPairsAsync(client))[pairIndex];
        await Bb.SetAvailableAsync(client, partId, warehouseId, available);
        var visitId = Bb.Id((await Bb.TodayAsync(client))[1]);
        return (client, visitId, partId, warehouseId);
    }

    // ---------- Katalog i stany ----------

    [Test]
    public async Task Katalog_ma_12_czesci_z_opisem_cena_i_zdjeciem()
    {
        var client = await Api.ClientAsync(_factory);

        var parts = Bb.Items(await Bb.GetOkAsync(client, "api/parts"));

        Assert.That(parts, Has.Count.EqualTo(12));
        Assert.That(parts.Select(p => Bb.Str(p, "id")), Is.Unique);
        Assert.That(parts.Select(p => Bb.Str(p, "id")), Does.Contain(Bb.LastPieceSku));
        foreach (var part in parts)
        {
            Assert.That(Bb.Str(part, "name"), Is.Not.Null.And.Not.Empty);
            Assert.That(Bb.Str(part, "description"), Is.Not.Null.And.Not.Empty);
            Assert.That(part.GetProperty("unitPrice").GetDouble(), Is.GreaterThan(0));
            Assert.That(Bb.Str(part, "imageUrl"), Is.Not.Null.And.Not.Empty);
            Assert.That(Bb.Str(part, "thumbnailUrl"), Is.Not.Null.And.Not.Empty);
        }
    }

    [Test]
    public async Task Sa_dwa_magazyny_MAG_01_i_MAG_02()
    {
        var client = await Api.ClientAsync(_factory);

        var warehouses = Bb.Items(await Bb.GetOkAsync(client, "api/warehouses"));

        Assert.That(warehouses.Select(w => Bb.Str(w, "id")), Is.EquivalentTo(new[] { "MAG-01", "MAG-02" }));
        Assert.That(warehouses.All(w => !string.IsNullOrEmpty(Bb.Str(w, "name"))), Is.True, "Magazyn bez nazwy");
    }

    [Test]
    public async Task Stan_czesci_dostepne_to_na_stanie_minus_zarezerwowane()
    {
        var client = await Api.ClientAsync(_factory);

        foreach (var partId in await Bb.PartIdsAsync(client))
        {
            foreach (var level in Bb.Items(await Bb.GetOkAsync(client, $"api/parts/{partId}/stock")))
            {
                Assert.That(Bb.Str(level, "partId"), Is.EqualTo(partId));
                Assert.That(Bb.Str(level, "warehouseId"), Is.EqualTo("MAG-01").Or.EqualTo("MAG-02"));
                Assert.That(Bb.Str(level, "warehouseName"), Is.Not.Null.And.Not.Empty);
                Assert.That(Bb.Int(level, "available"), Is.EqualTo(Bb.Int(level, "onHand") - Bb.Int(level, "reserved")), $"{partId}");
                Assert.That(Bb.Int(level, "reserved"), Is.LessThanOrEqualTo(Bb.Int(level, "onHand")), $"{partId}");
            }
        }
    }

    [Test]
    public async Task Stan_nieznanej_czesci_daje_404_PartNotFound()
    {
        var client = await Api.ClientAsync(_factory);

        var response = await client.GetAsync("api/parts/NIE-MA-TAKIEJ/stock");

        await Bb.ProblemAsync(response, HttpStatusCode.NotFound, "Inventory.PartNotFound");
    }

    // ---------- Rezerwacja i zwolnienie ----------

    [Test]
    public async Task Rezerwacja_zmniejsza_dostepnosc_a_zwolnienie_ja_przywraca()
    {
        var (client, visitId, partId, warehouseId) = await PrepareAsync(0, 5);

        var reserveResponse = await Bb.ReserveAsync(client, partId, warehouseId, visitId, 2);

        var reservation = await Bb.ExpectAsync(reserveResponse, HttpStatusCode.Created);
        Assert.That(Bb.Str(reservation, "partId"), Is.EqualTo(partId));
        Assert.That(Bb.Str(reservation, "warehouseId"), Is.EqualTo(warehouseId));
        Assert.That(reservation.GetProperty("visitId").GetGuid(), Is.EqualTo(visitId));
        Assert.That(Bb.Int(reservation, "quantity"), Is.EqualTo(2));
        Assert.That(Bb.Time(reservation, "expiresAt"), Is.EqualTo(DateTimeOffset.UtcNow.AddMinutes(30)).Within(TimeSpan.FromMinutes(2)));
        Assert.That(Bb.Int(await Bb.StockAsync(client, partId, warehouseId), "available"), Is.EqualTo(3));
        Assert.That((await Bb.ReservationsAsync(client)).Select(Bb.Id), Does.Contain(Bb.Id(reservation)));

        var release = await client.DeleteAsync($"api/parts/reservations/{Bb.Id(reservation)}");

        Assert.That(release.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(Bb.Int(await Bb.StockAsync(client, partId, warehouseId), "available"), Is.EqualTo(5));
        Assert.That((await Bb.ReservationsAsync(client)).Select(Bb.Id), Does.Not.Contain(Bb.Id(reservation)));
    }

    [Test]
    public async Task Ponowne_zwolnienie_i_nieznana_rezerwacja_daja_404_ReservationNotFound()
    {
        var (client, visitId, partId, warehouseId) = await PrepareAsync(1, 2);
        var reservation = await Bb.ExpectAsync(await Bb.ReserveAsync(client, partId, warehouseId, visitId), HttpStatusCode.Created);
        Assert.That((await client.DeleteAsync($"api/parts/reservations/{Bb.Id(reservation)}")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        await Bb.ProblemAsync(await client.DeleteAsync($"api/parts/reservations/{Bb.Id(reservation)}"), HttpStatusCode.NotFound, "Inventory.ReservationNotFound");
        await Bb.ProblemAsync(await client.DeleteAsync($"api/parts/reservations/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "Inventory.ReservationNotFound");
    }

    [Test]
    public async Task Rezerwacja_ponad_dostepne_daje_409_OutOfStock_z_polem_available()
    {
        var (client, visitId, partId, warehouseId) = await PrepareAsync(2, 2);

        var response = await Bb.ReserveAsync(client, partId, warehouseId, visitId, 3);

        var problem = await Bb.ProblemAsync(response, HttpStatusCode.Conflict, "Inventory.OutOfStock");
        Assert.That(Bb.Int(problem, "available"), Is.EqualTo(2));
        Assert.That(Bb.Int(await Bb.StockAsync(client, partId, warehouseId), "available"), Is.EqualTo(2));
    }

    [Test]
    public async Task KOMPRESOR_R32_w_MAG_01_zawsze_daje_409_OutOfStock()
    {
        var client = await Api.ClientAsync(_factory);
        var visitId = Bb.Id((await Bb.TodayAsync(client))[0]);
        var before = await Bb.StockAsync(client, Bb.LastPieceSku, Bb.MainWarehouse);
        Assert.That(Bb.Int(before, "available"), Is.EqualTo(0));
        Assert.That(Bb.Int(before, "reserved"), Is.GreaterThanOrEqualTo(1));

        var response = await Bb.ReserveAsync(client, Bb.LastPieceSku, Bb.MainWarehouse, visitId);

        var problem = await Bb.ProblemAsync(response, HttpStatusCode.Conflict, "Inventory.OutOfStock");
        Assert.That(Bb.Int(problem, "available"), Is.EqualTo(0));
        Assert.That((await Bb.ReservationsAsync(client)).Select(r => Bb.Str(r, "partId")), Does.Not.Contain(Bb.LastPieceSku));
    }

    [Test]
    public async Task Rownolegle_rezerwacje_ostatniej_sztuki_dokladnie_jedna_udana()
    {
        var (_, _, partId, warehouseId) = await PrepareAsync(3, 1);
        // Kilku techników naraz chce ostatnią sztukę – każdy dla swojej wizyty
        var technicians = new List<(HttpClient Client, Guid VisitId)>();
        for (var i = 0; i < 5; i++)
        {
            var client = await Api.ClientAsync(_factory);
            technicians.Add((client, Bb.Id((await Bb.TodayAsync(client))[1])));
        }

        var responses = await Task.WhenAll(technicians.Select(t => Bb.ReserveAsync(t.Client, partId, warehouseId, t.VisitId)));

        Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Created), Is.EqualTo(1),
            string.Join(", ", responses.Select(r => (int)r.StatusCode)));
        foreach (var rejected in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            var problem = await Bb.ProblemAsync(rejected, HttpStatusCode.Conflict, "Inventory.OutOfStock");
            Assert.That(Bb.Int(problem, "available"), Is.EqualTo(0));
        }
        var after = await Bb.StockAsync(technicians[0].Client, partId, warehouseId);
        Assert.That(Bb.Int(after, "available"), Is.EqualTo(0));
        Assert.That(Bb.Int(after, "reserved"), Is.EqualTo(Bb.Int(after, "onHand")));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public async Task Nieprawidlowa_ilosc_daje_422_InvalidQuantity(int quantity)
    {
        var (client, visitId, partId, warehouseId) = await PrepareAsync(4, 3);

        var response = await Bb.ReserveAsync(client, partId, warehouseId, visitId, quantity);

        await Bb.ProblemAsync(response, (HttpStatusCode)422, "Inventory.InvalidQuantity");
        Assert.That(Bb.Int(await Bb.StockAsync(client, partId, warehouseId), "available"), Is.EqualTo(3));
    }

    [Test]
    public async Task Rezerwacja_dla_wizyty_innego_technika_daje_404()
    {
        var (owner, visitId, partId, warehouseId) = await PrepareAsync(5, 3);
        var stranger = await Api.ClientAsync(_factory);

        var response = await Bb.ReserveAsync(stranger, partId, warehouseId, visitId);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), await response.Content.ReadAsStringAsync());
        Assert.That(Bb.Int(await Bb.StockAsync(owner, partId, warehouseId), "available"), Is.EqualTo(3));
    }

    [Test]
    public async Task Rezerwacja_dla_nieistniejacej_wizyty_daje_404()
    {
        var (client, _, partId, warehouseId) = await PrepareAsync(6, 3);

        var response = await Bb.ReserveAsync(client, partId, warehouseId, Guid.NewGuid());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task Rezerwacja_nieznanej_czesci_daje_404_PartNotFound()
    {
        var client = await Api.ClientAsync(_factory);
        var visitId = Bb.Id((await Bb.TodayAsync(client))[0]);

        var response = await Bb.ReserveAsync(client, "NIE-MA-TAKIEJ", Bb.MainWarehouse, visitId);

        await Bb.ProblemAsync(response, HttpStatusCode.NotFound, "Inventory.PartNotFound");
    }

    [Test]
    public async Task Rezerwacja_w_nieznanym_magazynie_daje_404_StockNotFound()
    {
        var client = await Api.ClientAsync(_factory);
        var visitId = Bb.Id((await Bb.TodayAsync(client))[0]);
        var partId = await Bb.AnyPartIdAsync(client);

        var response = await Bb.ReserveAsync(client, partId, "MAG-99", visitId);

        await Bb.ProblemAsync(response, HttpStatusCode.NotFound, "Inventory.StockNotFound");
    }

    [Test]
    public async Task Rezerwacji_innego_technika_nie_mozna_zwolnic_ani_zobaczyc()
    {
        var (owner, visitId, partId, warehouseId) = await PrepareAsync(7, 3);
        var reservation = await Bb.ExpectAsync(await Bb.ReserveAsync(owner, partId, warehouseId, visitId), HttpStatusCode.Created);
        var stranger = await Api.ClientAsync(_factory);

        Assert.That((await Bb.ReservationsAsync(stranger)).Select(Bb.Id), Does.Not.Contain(Bb.Id(reservation)));
        var release = await stranger.DeleteAsync($"api/parts/reservations/{Bb.Id(reservation)}");

        await Bb.ProblemAsync(release, HttpStatusCode.NotFound, "Inventory.ReservationNotFound");
        Assert.That((await Bb.ReservationsAsync(owner)).Select(Bb.Id), Does.Contain(Bb.Id(reservation)));
        Assert.That(Bb.Int(await Bb.StockAsync(owner, partId, warehouseId), "available"), Is.EqualTo(2));
    }
}
