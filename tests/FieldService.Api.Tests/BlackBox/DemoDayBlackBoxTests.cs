namespace FieldService.Api.Tests.BlackBox;

/// <summary>
/// Dzień pracy tworzony przy pierwszym logowaniu: 4 wizyty na dziś (8:00 rozpoczęta, 10:00, 12:30, 14:30),
/// zlecenia, auto technika (6 części), historia (20 wizyt; konto wydajności – Demo:PerformanceHistoryVisits = 120).
/// Testy tylko czytają dane, więc mogą dzielić bazę klasy.
/// </summary>
public class DemoDayBlackBoxTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Nowy_technik_ma_cztery_wizyty_na_dzis_od_najwczesniejszej()
    {
        var client = await Api.ClientAsync(_factory);

        var today = await Bb.TodayAsync(client);

        Assert.That(today, Has.Count.EqualTo(4));
        var starts = today.Select(v => Bb.Time(v, "plannedStart")).ToList();
        Assert.That(starts, Is.Ordered);
        Assert.That(starts.Select(s => Bb.ToWarsaw(s).Date), Is.All.EqualTo(Bb.WarsawToday));
        Assert.That(starts.Select(s => Bb.ToWarsaw(s).ToString("HH:mm")), Is.EqualTo(new[] { "08:00", "10:00", "12:30", "14:30" }));
        Assert.That(today.Select(Bb.Id), Is.Unique);
        Assert.That(today.All(v => Bb.Time(v, "plannedEnd") > Bb.Time(v, "plannedStart")), Is.True);
    }

    [Test]
    public async Task Szacowany_przyjazd_to_poczatek_okna_dla_zaplanowanej_i_pusty_dla_zakonczonej()
    {
        var client = await Api.ClientAsync(_factory);

        var today = await Bb.TodayAsync(client);

        Assert.Multiple(() =>
        {
            foreach (var visit in today)
            {
                var status = Bb.Status(visit);
                if (status == "Planned")
                    Assert.That(Bb.Time(visit, "estimatedArrival"), Is.EqualTo(Bb.Time(visit, "plannedStart")));
                else if (status == "InProgress")
                    Assert.That(Bb.IsNullOrMissing(visit, "estimatedArrival"), Is.False);
                else
                    Assert.That(Bb.IsNullOrMissing(visit, "estimatedArrival"), Is.True);
            }
        });
    }

    [Test]
    public async Task Wizyta_8_00_jest_rozpoczeta_a_pozostale_zaplanowane()
    {
        var client = await Api.ClientAsync(_factory);

        var today = await Bb.TodayAsync(client);

        Assert.That(today.Select(Bb.Status), Is.EqualTo(new[] { "InProgress", "Planned", "Planned", "Planned" }));
        var started = await Bb.VisitAsync(client, Bb.Id(today[0]));
        Assert.That(Bb.IsNullOrMissing(started, "startedAt"), Is.False, "Rozpoczęta wizyta powinna mieć startedAt");
    }

    [Test]
    public async Task Kazda_wizyta_ma_zlecenie_klienta_urzadzenie_i_opis_usterki()
    {
        var email = Api.NewEmail();
        var client = await Api.ClientAsync(_factory, email);

        foreach (var summary in await Bb.TodayAsync(client))
        {
            Assert.That(Bb.Str(summary, "workOrderNumber"), Is.Not.Null.And.Not.Empty);
            Assert.That(Bb.Str(summary, "customerName"), Is.Not.Null.And.Not.Empty);
            Assert.That(Bb.Str(summary, "address"), Is.Not.Null.And.Not.Empty);
            Assert.That(Bb.Str(summary, "deviceName"), Is.Not.Null.And.Not.Empty);
            Assert.That(Bb.Str(summary, "faultDescription"), Is.Not.Null.And.Not.Empty);
            Assert.That(summary.GetProperty("isBrigade").GetBoolean(), Is.False);

            var details = await Bb.VisitAsync(client, Bb.Id(summary));
            Assert.That(Bb.Version(details), Is.EqualTo(Bb.Version(summary)));
            Assert.That(details.GetProperty("workOrderId").GetGuid(), Is.EqualTo(summary.GetProperty("workOrderId").GetGuid()));
            var customer = details.GetProperty("customer");
            foreach (var field in new[] { "name", "phone", "street", "city" })
                Assert.That(Bb.Str(customer, field), Is.Not.Null.And.Not.Empty, field);
            Assert.That(Bb.Items(details.GetProperty("technicianEmails")).Select(e => e.GetString()!.ToLowerInvariant()),
                Is.EqualTo(new[] { email.ToLowerInvariant() }));
        }
    }

    [Test]
    public async Task Drugie_logowanie_tego_samego_dnia_nie_dubluje_danych()
    {
        var email = Api.NewEmail();
        var first = await Api.ClientAsync(_factory, email);
        var idsFirst = (await Bb.TodayAsync(first)).Select(Bb.Id).ToList();

        var second = await Api.ClientAsync(_factory, email);

        var idsSecond = (await Bb.TodayAsync(second)).Select(Bb.Id).ToList();
        Assert.That(idsSecond, Is.EquivalentTo(idsFirst));
        Assert.That(Bb.Int(await Bb.GetOkAsync(second, "api/visits/history"), "totalCount"), Is.EqualTo(20));
        Assert.That(Bb.Items(await Bb.GetOkAsync(second, "api/vanstock")), Has.Count.EqualTo(6));
        Assert.That(Bb.Items(await Bb.GetOkAsync(second, "api/workorders")).Select(Bb.Id), Is.Unique);
    }

    [Test]
    public async Task Kazdy_email_to_osobny_technik_z_wlasnymi_wizytami()
    {
        var a = await Api.ClientAsync(_factory);
        var b = await Api.ClientAsync(_factory);

        var idsA = (await Bb.TodayAsync(a)).Select(Bb.Id).ToList();
        var idsB = (await Bb.TodayAsync(b)).Select(Bb.Id).ToList();

        Assert.That(idsA.Intersect(idsB), Is.Empty);
    }

    [Test]
    public async Task Historia_ma_20_zakonczonych_lub_anulowanych_wizyt_z_poprzednich_dni()
    {
        var client = await Api.ClientAsync(_factory);

        var page = await Bb.GetOkAsync(client, "api/visits/history?pageSize=100");

        Assert.That(Bb.Int(page, "totalCount"), Is.EqualTo(20));
        var items = Bb.Items(page.GetProperty("items"));
        Assert.That(items, Has.Count.EqualTo(20));
        Assert.That(items.All(i => Bb.Status(i) is "Completed" or "Cancelled"), Is.True, "Historia: tylko Completed i Cancelled");
        Assert.That(items.Select(i => Bb.ToWarsaw(Bb.Time(i, "plannedStart")).Date), Is.All.LessThan(Bb.WarsawToday));
    }

    [Test]
    public async Task Auto_technika_ma_6_czesci_z_katalogu()
    {
        var client = await Api.ClientAsync(_factory);
        var catalog = await Bb.PartIdsAsync(client);

        var van = Bb.Items(await Bb.GetOkAsync(client, "api/vanstock"));

        Assert.That(van, Has.Count.EqualTo(6));
        foreach (var item in van)
        {
            Assert.That(catalog, Does.Contain(Bb.Str(item, "partId")!));
            Assert.That(Bb.Str(item, "partName"), Is.Not.Null.And.Not.Empty);
            Assert.That(Bb.Str(item, "thumbnailUrl"), Is.Not.Null.And.Not.Empty);
            Assert.That(Bb.Int(item, "quantity"), Is.GreaterThan(0));
        }
    }

    [Test]
    public async Task Moje_zlecenia_zawieraja_otwarte_zlecenia_wizyt_na_dzis()
    {
        var client = await Api.ClientAsync(_factory);
        var today = await Bb.TodayAsync(client);

        var orders = Bb.Items(await Bb.GetOkAsync(client, "api/workorders"));

        Assert.That(orders, Has.Count.GreaterThanOrEqualTo(4));
        foreach (var visit in today)
        {
            var order = orders.FirstOrDefault(o => Bb.Id(o) == visit.GetProperty("workOrderId").GetGuid());
            Assert.That(order.ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Object), "Brak zlecenia wizyty na dziś");
            Assert.That(Bb.Str(order, "number"), Is.EqualTo(Bb.Str(visit, "workOrderNumber")));
            Assert.That(Bb.Status(order), Is.EqualTo("Open"));
            Assert.That(Bb.Int(order, "visitCount"), Is.GreaterThanOrEqualTo(1));
            Assert.That(Bb.Str(order, "customerName"), Is.Not.Null.And.Not.Empty);
        }
        // Na świeżym dniu nic nie jest zamknięte: lista to zlecenia otwarte albo z wizytą na dziś
        Assert.That(orders.Select(Bb.Status), Is.All.EqualTo("Open"));
    }

    [Test]
    public async Task Konto_wydajnosci_ma_historie_z_konfiguracji()
    {
        var client = await Api.ClientAsync(_factory, Bb.PerformanceEmail);

        var page = await Bb.GetOkAsync(client, "api/visits/history");

        // W harnessie Demo:PerformanceHistoryVisits = 120 (na produkcji 2000)
        Assert.That(Bb.Int(page, "totalCount"), Is.EqualTo(120));
        Assert.That(await Bb.TodayAsync(client), Has.Count.EqualTo(4));
    }
}
