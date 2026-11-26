using System.Net;
using System.Net.Http.Json;

namespace FieldService.Api.Tests.BlackBox;

/// <summary>Reset jednego technika (nagłówek X-Admin-Key): przy następnym logowaniu powstaje nowy dzień pracy.</summary>
public class AdminResetBlackBoxTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Reset_bez_klucza_daje_401()
    {
        var client = _factory.CreateClient();

        var response = await Bb.AdminResetAsync(client, key: null, technicianEmail: Api.NewEmail());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Reset_z_blednym_kluczem_daje_401_i_nic_nie_usuwa()
    {
        var email = Api.NewEmail();
        var phone = await Api.ClientAsync(_factory, email);
        var ids = (await Bb.TodayAsync(phone)).Select(Bb.Id).ToList();

        var response = await Bb.AdminResetAsync(_factory.CreateClient(), "zly-klucz", email);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        var again = await Api.ClientAsync(_factory, email);
        Assert.That((await Bb.TodayAsync(again)).Select(Bb.Id), Is.EquivalentTo(ids));
    }

    [Test]
    public async Task Reset_jednego_technika_daje_nowy_dzien_przy_nastepnym_logowaniu()
    {
        var email = Api.NewEmail();
        var anonymous = _factory.CreateClient();
        var login = await Bb.LoginJsonAsync(anonymous, email);
        var phone = _factory.CreateClient();
        Bb.UseToken(phone, Bb.Str(login, "accessToken"));
        var oldIds = (await Bb.TodayAsync(phone)).Select(Bb.Id).ToList();
        var other = await Api.ClientAsync(_factory);
        var otherIds = (await Bb.TodayAsync(other)).Select(Bb.Id).ToList();

        var result = await Bb.OkAsync(await Bb.AdminResetAsync(anonymous, ApiFactory.AdminKey, email));

        Assert.That(Bb.Int(result, "removedTechnicians"), Is.EqualTo(1));
        Assert.That(Bb.Int(result, "removedVisits"), Is.GreaterThanOrEqualTo(24), "4 wizyty na dziś + 20 w historii");
        Assert.That(Bb.Str(result, "message"), Is.Not.Null.And.Not.Empty);

        // Sesje technika usunięte – stary refresh token nie działa
        var refresh = await anonymous.PostAsJsonAsync("api/auth/refresh", new { refreshToken = Bb.Str(login, "refreshToken") });
        Assert.That(refresh.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        // Nowe logowanie – nowy dzień pracy
        var fresh = await Api.ClientAsync(_factory, email);
        var today = await Bb.TodayAsync(fresh);
        Assert.That(today, Has.Count.EqualTo(4));
        Assert.That(today.Select(Bb.Id).Intersect(oldIds), Is.Empty);
        Assert.That(today.Select(Bb.Status), Is.EqualTo(new[] { "InProgress", "Planned", "Planned", "Planned" }));
        Assert.That(Bb.Int(await Bb.GetOkAsync(fresh, "api/visits/history"), "totalCount"), Is.EqualTo(20));
        Assert.That(Bb.Items(await Bb.GetOkAsync(fresh, "api/vanstock")), Has.Count.EqualTo(6));

        // Inny technik bez zmian
        Assert.That((await Bb.TodayAsync(other)).Select(Bb.Id), Is.EquivalentTo(otherIds));
    }

    [Test]
    public async Task Reset_technika_usuwa_jego_rezerwacje()
    {
        var email = Api.NewEmail();
        var phone = await Api.ClientAsync(_factory, email);
        var (partId, warehouseId) = (await Bb.StockPairsAsync(phone))[0];
        await Bb.SetAvailableAsync(phone, partId, warehouseId, 4);
        await Bb.ExpectAsync(await Bb.ReserveAsync(phone, partId, warehouseId, Bb.Id((await Bb.TodayAsync(phone))[0]), 3), HttpStatusCode.Created);

        await Bb.OkAsync(await Bb.AdminResetAsync(_factory.CreateClient(), ApiFactory.AdminKey, email));

        var fresh = await Api.ClientAsync(_factory, email);
        Assert.That(await Bb.ReservationsAsync(fresh), Is.Empty);
        Assert.That(Bb.Int(await Bb.StockAsync(fresh, partId, warehouseId), "available"), Is.EqualTo(4));
    }

    [Test]
    public async Task Reset_technika_brygady_usuwa_wspolna_wizyte_takze_u_drugiego()
    {
        var first = await Api.ClientAsync(_factory, Bb.BrigadeEmail1);
        var second = await Api.ClientAsync(_factory, Bb.BrigadeEmail2);
        var shared = Bb.Id((await Bb.TodayAsync(first)).Single(v => v.GetProperty("isBrigade").GetBoolean()));
        Assert.That((await Bb.TodayAsync(second)).Select(Bb.Id), Does.Contain(shared));

        await Bb.OkAsync(await Bb.AdminResetAsync(_factory.CreateClient(), ApiFactory.AdminKey, Bb.BrigadeEmail1));

        Assert.That((await Bb.TodayAsync(second)).Select(Bb.Id), Does.Not.Contain(shared));
        Assert.That((await second.GetAsync($"api/visits/{shared}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}

/// <summary>
/// Pełny reset (bez technicianEmail): cała baza od nowa, katalog i zapasy jak po starcie,
/// z rezerwacją ostatniej sztuki KOMPRESOR-R32 w MAG-01. Osobna klasa = osobna baza.
/// </summary>
public class AdminFullResetBlackBoxTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Pelny_reset_przywraca_katalog_zapasy_i_rezerwacje_KOMPRESOR_R32()
    {
        // Stan początkowy
        var phone = await Api.ClientAsync(_factory);
        var catalog = await Bb.PartIdsAsync(phone);
        var (partId, warehouseId) = (await Bb.StockPairsAsync(phone))[0];
        var original = await Bb.StockAsync(phone, partId, warehouseId);
        var compressorBefore = await Bb.StockAsync(phone, Bb.LastPieceSku, Bb.MainWarehouse);

        // Zmiany: dostawa, rezerwacja i zwiększenie stanu KOMPRESOR-R32 (żeby dało się go zarezerwować)
        await Bb.SetAvailableAsync(phone, partId, warehouseId, Bb.Int(original, "available") + 10);
        await Bb.ExpectAsync(await Bb.ReserveAsync(phone, partId, warehouseId, Bb.Id((await Bb.TodayAsync(phone))[0]), 2), HttpStatusCode.Created);
        await Bb.SetAvailableAsync(phone, Bb.LastPieceSku, Bb.MainWarehouse, 3);

        var result = await Bb.OkAsync(await Bb.AdminResetAsync(_factory.CreateClient(), ApiFactory.AdminKey));
        Assert.That(Bb.Int(result, "removedTechnicians"), Is.GreaterThanOrEqualTo(1));

        // Po resecie: katalog i zapasy jak na początku
        var after = await Api.ClientAsync(_factory);
        Assert.That(await Bb.PartIdsAsync(after), Is.EquivalentTo(catalog));
        var restored = await Bb.StockAsync(after, partId, warehouseId);
        Assert.That(Bb.Int(restored, "onHand"), Is.EqualTo(Bb.Int(original, "onHand")));
        Assert.That(Bb.Int(restored, "reserved"), Is.EqualTo(Bb.Int(original, "reserved")));
        var compressor = await Bb.StockAsync(after, Bb.LastPieceSku, Bb.MainWarehouse);
        Assert.That(Bb.Int(compressor, "onHand"), Is.EqualTo(Bb.Int(compressorBefore, "onHand")));
        Assert.That(Bb.Int(compressor, "reserved"), Is.GreaterThanOrEqualTo(1));
        Assert.That(Bb.Int(compressor, "available"), Is.EqualTo(0));

        // Rezerwacja ostatniej sztuki znów kończy się konfliktem
        var reserve = await Bb.ReserveAsync(after, Bb.LastPieceSku, Bb.MainWarehouse, Bb.Id((await Bb.TodayAsync(after))[0]));
        await Bb.ProblemAsync(reserve, HttpStatusCode.Conflict, "Inventory.OutOfStock");
    }

    [Test]
    public async Task Pelny_reset_bez_klucza_daje_401()
    {
        var response = await Bb.AdminResetAsync(_factory.CreateClient(), key: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
