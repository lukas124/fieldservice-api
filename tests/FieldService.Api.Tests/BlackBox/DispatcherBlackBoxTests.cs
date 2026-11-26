using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FieldService.Api.Tests.BlackBox;

/// <summary>
/// Symulacja dyspozytora (bez logowania): lista wizyt, anulowanie (R6), przeniesienie, nowa wizyta, stan magazynu.
/// Każda zmiana wizyty podnosi wersję – telefon ze starą wersją dostaje 409.
/// </summary>
public class DispatcherBlackBoxTests
{
    private ApiFactory _factory = null!;
    private HttpClient _dispatcher = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _factory = new ApiFactory();
        _dispatcher = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _dispatcher.Dispose();
        _factory.Dispose();
    }

    private Task<HttpResponseMessage> CancelAsync(Guid visitId, string reason = "Klient odwołał wizytę telefonicznie") =>
        _dispatcher.PostAsJsonAsync($"api/dispatcher/visits/{visitId}/cancel", new { reason });

    private Task<HttpResponseMessage> RescheduleAsync(Guid visitId, DateTimeOffset plannedStart, DateTimeOffset plannedEnd) =>
        _dispatcher.PostAsJsonAsync($"api/dispatcher/visits/{visitId}/reschedule", new { plannedStart, plannedEnd });

    private async Task<List<JsonElement>> DispatcherVisitsAsync(string email) =>
        Bb.Items(await Bb.GetOkAsync(_dispatcher, $"api/dispatcher/visits?technicianEmail={Uri.EscapeDataString(email)}"));

    // ---------- Lista ----------

    [Test]
    public async Task Lista_dyspozytora_pokazuje_wizyty_technika_z_wersjami()
    {
        var email = Api.NewEmail();
        var phone = await Api.ClientAsync(_factory, email);
        var today = await Bb.TodayAsync(phone);

        var list = await DispatcherVisitsAsync(email);

        Assert.That(list.Select(Bb.Id), Is.EquivalentTo(today.Select(Bb.Id)));
        foreach (var visit in list)
        {
            var seenByPhone = today.Single(v => Bb.Id(v) == Bb.Id(visit));
            Assert.That(Bb.Version(visit), Is.EqualTo(Bb.Version(seenByPhone)));
            Assert.That(Bb.Status(visit), Is.EqualTo(Bb.Status(seenByPhone)));
            Assert.That(Bb.Items(visit.GetProperty("technicianEmails")).Select(e => e.GetString()!.ToLowerInvariant()), Does.Contain(email));
            Assert.That(Bb.Str(visit, "workOrderNumber"), Is.EqualTo(Bb.Str(seenByPhone, "workOrderNumber")));
        }
    }

    [Test]
    public async Task Lista_wszystkich_zawiera_wizyty_wielu_technikow()
    {
        var a = await Api.ClientAsync(_factory);
        var b = await Api.ClientAsync(_factory);
        var expected = (await Bb.TodayAsync(a)).Concat(await Bb.TodayAsync(b)).Select(Bb.Id).ToList();

        var all = Bb.Items(await Bb.GetOkAsync(_dispatcher, "api/dispatcher/visits")).Select(Bb.Id).ToList();

        Assert.That(all, Is.SupersetOf(expected));
    }

    // ---------- Anulowanie (R6) ----------

    [Test]
    public async Task Anulowanie_zaplanowanej_wizyty_podnosi_wersje_a_telefon_ze_stara_wersja_dostaje_409()
    {
        var phone = await Api.ClientAsync(_factory);
        var planned = await Bb.FirstWithStatusAsync(phone, "Planned");

        var cancelled = await Bb.OkAsync(await CancelAsync(Bb.Id(planned), "Klient wyjechał"));

        Assert.That(Bb.Status(cancelled), Is.EqualTo("Cancelled"));
        Assert.That(Bb.Version(cancelled), Is.EqualTo(Bb.Version(planned) + 1));
        var details = await Bb.VisitAsync(phone, Bb.Id(planned));
        Assert.That(Bb.Status(details), Is.EqualTo("Cancelled"));
        Assert.That(Bb.Str(details, "cancelReason"), Is.EqualTo("Klient wyjechał"));
        Assert.That(Bb.Version(details), Is.EqualTo(Bb.Version(planned) + 1));

        // Telefon nie wie o anulowaniu – komenda ze starą wersją → 409 z aktualnym stanem
        var stale = await Api.CommandAsync(phone, HttpMethod.Post, $"api/visits/{Bb.Id(planned)}/start", new { }, Bb.Version(planned));
        var problem = await Bb.ProblemAsync(stale, HttpStatusCode.Conflict, "Visit.VersionConflict");
        Assert.That(Bb.Int(problem, "currentVersion"), Is.EqualTo(Bb.Version(planned) + 1));
        Assert.That(Bb.Str(problem, "currentStatus"), Is.EqualTo("Cancelled"));

        // Z aktualną wersją: anulowanej nie da się rozpocząć (R1)
        var fresh = await Api.CommandAsync(phone, HttpMethod.Post, $"api/visits/{Bb.Id(planned)}/start", new { }, Bb.Version(details));
        await Bb.ProblemAsync(fresh, HttpStatusCode.Conflict, "Visit.InvalidTransition");
    }

    [Test]
    public async Task Anulowanie_wizyty_w_trakcie_blokuje_dalsza_prace_na_telefonie()
    {
        var phone = await Api.ClientAsync(_factory);
        var inProgress = await Bb.FirstWithStatusAsync(phone, "InProgress");

        var cancelled = await Bb.OkAsync(await CancelAsync(Bb.Id(inProgress)));

        Assert.That(Bb.Status(cancelled), Is.EqualTo("Cancelled"));
        var details = await Bb.VisitAsync(phone, Bb.Id(inProgress));
        var activity = await Api.CommandAsync(phone, HttpMethod.Post, $"api/visits/{Bb.Id(inProgress)}/activities",
            new { code = "DIAG", description = "Po anulowaniu" }, Bb.Version(details));
        await Bb.ProblemAsync(activity, HttpStatusCode.Conflict, "Visit.NotInProgress");
    }

    [Test]
    public async Task Anulowanie_zakonczonej_wizyty_daje_409_AlreadyCompleted()
    {
        var phone = await Api.ClientAsync(_factory);
        var inProgress = await Bb.FirstWithStatusAsync(phone, "InProgress");
        var completed = await Bb.OkAsync(await Bb.CompleteAsync(phone, inProgress, "NotFixed", "Brak dostępu do kotłowni"));

        var response = await CancelAsync(Bb.Id(completed));

        await Bb.ProblemAsync(response, HttpStatusCode.Conflict, "Visit.AlreadyCompleted");
        var after = await Bb.VisitAsync(phone, Bb.Id(completed));
        Assert.That(Bb.Status(after), Is.EqualTo("Completed"));
        Assert.That(Bb.Version(after), Is.EqualTo(Bb.Version(completed)));
    }

    [Test]
    public async Task Anulowanie_nieistniejacej_wizyty_daje_404()
    {
        var response = await CancelAsync(Guid.NewGuid());

        await Bb.ProblemAsync(response, HttpStatusCode.NotFound, "Visit.NotFound");
    }

    // ---------- Przeniesienie ----------

    [Test]
    public async Task Przeniesienie_zaplanowanej_wizyty_zmienia_termin_i_wersje()
    {
        var phone = await Api.ClientAsync(_factory);
        var planned = await Bb.FirstWithStatusAsync(phone, "Planned");
        var newStart = Bb.Time(planned, "plannedStart").AddMinutes(30);
        var newEnd = newStart.AddMinutes(90);

        var moved = await Bb.OkAsync(await RescheduleAsync(Bb.Id(planned), newStart, newEnd));

        Assert.That(Bb.Time(moved, "plannedStart"), Is.EqualTo(newStart));
        Assert.That(Bb.Version(moved), Is.EqualTo(Bb.Version(planned) + 1));
        Assert.That(Bb.Status(moved), Is.EqualTo("Planned"));
        var details = await Bb.VisitAsync(phone, Bb.Id(planned));
        Assert.That(Bb.Time(details, "plannedStart"), Is.EqualTo(newStart));
        Assert.That(Bb.Time(details, "plannedEnd"), Is.EqualTo(newEnd));
    }

    [Test]
    public async Task Po_przeniesieniu_telefon_ze_stara_wersja_dostaje_409_a_po_odswiezeniu_moze_rozpoczac()
    {
        var phone = await Api.ClientAsync(_factory);
        var planned = await Bb.FirstWithStatusAsync(phone, "Planned");
        var newStart = Bb.Time(planned, "plannedStart").AddMinutes(15);
        await Bb.OkAsync(await RescheduleAsync(Bb.Id(planned), newStart, newStart.AddHours(1)));

        var stale = await Api.CommandAsync(phone, HttpMethod.Post, $"api/visits/{Bb.Id(planned)}/start", new { }, Bb.Version(planned));

        var problem = await Bb.ProblemAsync(stale, HttpStatusCode.Conflict, "Visit.VersionConflict");
        Assert.That(Bb.Int(problem, "currentVersion"), Is.EqualTo(Bb.Version(planned) + 1));
        Assert.That(Bb.Str(problem, "currentStatus"), Is.EqualTo("Planned"));
        // Kompensacja: telefon bierze aktualną wersję i powtarza komendę
        var started = await Bb.OkCommandAsync(phone, HttpMethod.Post, $"api/visits/{Bb.Id(planned)}/start", new { }, Bb.Int(problem, "currentVersion"));
        Assert.That(Bb.Status(started), Is.EqualTo("InProgress"));
    }

    [Test]
    public async Task Przeniesienie_wizyty_w_trakcie_daje_409()
    {
        var phone = await Api.ClientAsync(_factory);
        var inProgress = await Bb.FirstWithStatusAsync(phone, "InProgress");
        var start = Bb.WarsawTodayAt(16, 0);

        var response = await RescheduleAsync(Bb.Id(inProgress), start, start.AddHours(1));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), await response.Content.ReadAsStringAsync());
        var after = await Bb.VisitAsync(phone, Bb.Id(inProgress));
        Assert.That(Bb.Version(after), Is.EqualTo(Bb.Version(inProgress)));
        Assert.That(Bb.Time(after, "plannedStart"), Is.EqualTo(Bb.Time(inProgress, "plannedStart")));
    }

    [Test]
    public async Task Przeniesienie_z_koncem_przed_poczatkiem_daje_422()
    {
        var phone = await Api.ClientAsync(_factory);
        var planned = await Bb.FirstWithStatusAsync(phone, "Planned");
        var start = Bb.Time(planned, "plannedStart");

        var response = await RescheduleAsync(Bb.Id(planned), start, start.AddHours(-1));

        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)422), await response.Content.ReadAsStringAsync());
        Assert.That(Bb.Version(await Bb.VisitAsync(phone, Bb.Id(planned))), Is.EqualTo(Bb.Version(planned)));
    }

    [Test]
    public async Task Przeniesienie_nieistniejacej_wizyty_daje_404()
    {
        var start = Bb.WarsawTodayAt(16, 0);

        var response = await RescheduleAsync(Guid.NewGuid(), start, start.AddHours(1));

        await Bb.ProblemAsync(response, HttpStatusCode.NotFound, "Visit.NotFound");
    }

    [Test]
    public async Task Zmiana_dyspozytora_zmienia_ETag_listy_na_dzis()
    {
        var phone = await Api.ClientAsync(_factory);
        var first = await phone.GetAsync("api/visits/today");
        var etag = first.Headers.ETag?.ToString() ?? throw new AssertionException("Lista na dziś bez ETag");
        var planned = await Bb.FirstWithStatusAsync(phone, "Planned");
        var newStart = Bb.Time(planned, "plannedStart").AddMinutes(10);
        await Bb.OkAsync(await RescheduleAsync(Bb.Id(planned), newStart, newStart.AddHours(1)));

        var again = await Bb.GetIfNoneMatchAsync(phone, "api/visits/today", etag);

        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(again.Headers.ETag?.ToString(), Is.Not.EqualTo(etag));
    }

    // ---------- Nowa wizyta ----------

    [Test]
    public async Task Nowa_wizyta_dla_technika_pojawia_sie_na_dzis_i_w_zleceniach()
    {
        var email = Api.NewEmail();
        var phone = await Api.ClientAsync(_factory, email);
        var plannedStart = Bb.WarsawTodayAt(18, 0);

        var created = await Bb.ExpectAsync(await _dispatcher.PostAsJsonAsync("api/dispatcher/visits",
            new { technicianEmail = email, plannedStart, faultDescription = "Pompa ciepła nie grzeje, błąd F.022" }), HttpStatusCode.Created);

        Assert.That(Bb.Status(created), Is.EqualTo("Planned"));
        Assert.That(Bb.Time(created, "plannedStart"), Is.EqualTo(plannedStart));
        Assert.That(Bb.Str(created, "workOrderNumber"), Is.Not.Null.And.Not.Empty);
        var today = await Bb.TodayAsync(phone);
        Assert.That(today, Has.Count.EqualTo(5));
        var summary = today.Single(v => Bb.Id(v) == Bb.Id(created));
        Assert.That(Bb.Version(summary), Is.EqualTo(Bb.Version(created)));
        Assert.That(summary.GetProperty("isBrigade").GetBoolean(), Is.False);
        Assert.That(Bb.Str(summary, "faultDescription"), Is.EqualTo("Pompa ciepła nie grzeje, błąd F.022"));
        var orders = Bb.Items(await Bb.GetOkAsync(phone, "api/workorders"));
        Assert.That(orders.Select(o => Bb.Str(o, "number")), Does.Contain(Bb.Str(created, "workOrderNumber")!));

        // Nową wizytę telefon może od razu rozpocząć
        var started = await Bb.OkCommandAsync(phone, HttpMethod.Post, $"api/visits/{Bb.Id(created)}/start", new { }, Bb.Version(created));
        Assert.That(Bb.Status(started), Is.EqualTo("InProgress"));
    }

    [Test]
    public async Task Nowa_wizyta_brygady_jest_wspolna_dla_obu_technikow()
    {
        var email1 = Api.NewEmail("brygada");
        var email2 = Api.NewEmail("brygada");
        var phone1 = await Api.ClientAsync(_factory, email1);
        var phone2 = await Api.ClientAsync(_factory, email2);

        var created = await Bb.ExpectAsync(await _dispatcher.PostAsJsonAsync("api/dispatcher/visits",
            new { technicianEmail = email1, secondTechnicianEmail = email2, plannedStart = Bb.WarsawTodayAt(17, 0) }), HttpStatusCode.Created);

        Assert.That(Bb.Items(created.GetProperty("technicianEmails")), Has.Count.EqualTo(2));
        foreach (var phone in new[] { phone1, phone2 })
        {
            var summary = (await Bb.TodayAsync(phone)).Single(v => Bb.Id(v) == Bb.Id(created));
            Assert.That(summary.GetProperty("isBrigade").GetBoolean(), Is.True);
        }
    }

    [Test]
    public async Task Nowa_wizyta_bez_terminu_jest_za_godzine_z_oknem_90_minut()
    {
        var email = Api.NewEmail();
        var phone = await Api.ClientAsync(_factory, email);
        var before = DateTimeOffset.UtcNow;

        var created = await Bb.ExpectAsync(await _dispatcher.PostAsJsonAsync("api/dispatcher/visits", new { technicianEmail = email }), HttpStatusCode.Created);

        var details = await Bb.VisitAsync(phone, Bb.Id(created));
        var start = Bb.Time(details, "plannedStart");
        Assert.That(start, Is.EqualTo(before.AddHours(1)).Within(TimeSpan.FromMinutes(5)));
        Assert.That(Bb.Time(details, "plannedEnd") - start, Is.EqualTo(TimeSpan.FromMinutes(90)));
    }

    [TestCase("")]
    [TestCase("bez-malpy")]
    public async Task Nowa_wizyta_z_niepoprawnym_emailem_daje_422(string technicianEmail)
    {
        var response = await _dispatcher.PostAsJsonAsync("api/dispatcher/visits", new { technicianEmail });

        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)422), await response.Content.ReadAsStringAsync());
    }

    // ---------- Stan magazynu ----------

    [Test]
    public async Task Stan_magazynu_ustawiony_przez_dyspozytora_widzi_technik()
    {
        var phone = await Api.ClientAsync(_factory);
        var (partId, warehouseId) = (await Bb.StockPairsAsync(phone))[0];
        var before = await Bb.StockAsync(phone, partId, warehouseId);
        var onHand = Bb.Int(before, "reserved") + 7;

        var updated = await Bb.OkAsync(await _dispatcher.PutAsJsonAsync($"api/dispatcher/stock/{partId}/{warehouseId}", new { onHand }));

        Assert.That(Bb.Int(updated, "onHand"), Is.EqualTo(onHand));
        Assert.That(Bb.Int(updated, "available"), Is.EqualTo(7));
        Assert.That(Bb.Str(updated, "partId"), Is.EqualTo(partId));
        Assert.That(Bb.Str(updated, "warehouseId"), Is.EqualTo(warehouseId));
        var seen = await Bb.StockAsync(phone, partId, warehouseId);
        Assert.That(Bb.Int(seen, "onHand"), Is.EqualTo(onHand));
        Assert.That(Bb.Int(seen, "available"), Is.EqualTo(7));
    }

    [Test]
    public async Task Zmiana_stanu_zachowuje_rezerwacje()
    {
        var phone = await Api.ClientAsync(_factory);
        var (partId, warehouseId) = (await Bb.StockPairsAsync(phone))[1];
        await Bb.SetAvailableAsync(phone, partId, warehouseId, 3);
        var visit = (await Bb.TodayAsync(phone))[0];
        await Bb.ExpectAsync(await Bb.ReserveAsync(phone, partId, warehouseId, Bb.Id(visit), 2), HttpStatusCode.Created);
        var reserved = Bb.Int(await Bb.StockAsync(phone, partId, warehouseId), "reserved");

        var updated = await Bb.OkAsync(await _dispatcher.PutAsJsonAsync($"api/dispatcher/stock/{partId}/{warehouseId}", new { onHand = reserved + 10 }));

        Assert.That(Bb.Int(updated, "reserved"), Is.EqualTo(reserved));
        Assert.That(Bb.Int(updated, "available"), Is.EqualTo(10));
        Assert.That((await Bb.ReservationsAsync(phone)).Select(r => Bb.Str(r, "partId")), Does.Contain(partId));
    }

    [Test]
    public async Task Stan_nieznanej_czesci_daje_404()
    {
        var response = await _dispatcher.PutAsJsonAsync("api/dispatcher/stock/NIE-MA-TAKIEJ/MAG-01", new { onHand = 5 });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
