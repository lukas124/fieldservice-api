using System.Net;

namespace FieldService.Api.Tests.BlackBox;

/// <summary>
/// Komendy wizyty przez API: reguły R1–R7, nagłówki If-Match (wersja) i Idempotency-Key.
/// Każdy test loguje nowego technika, więc testy nie dzielą wizyt.
/// </summary>
public class VisitCommandsBlackBoxTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    private static readonly string[] AllCommands = ["start", "activities", "parts", "remove-part", "signature", "complete"];

    // ---------- If-Match ----------

    [TestCaseSource(nameof(AllCommands))]
    public async Task Komenda_bez_If_Match_daje_428_i_nic_nie_zmienia(string command)
    {
        var client = await Api.ClientAsync(_factory);
        var visit = command == "start" ? await Bb.FirstWithStatusAsync(client, "Planned") : await Bb.StartPlannedAsync(client);
        var (method, url, body) = Bb.Command(command, Bb.Id(visit), await Bb.AnyPartIdAsync(client));

        var response = await Api.CommandAsync(client, method, url, body, version: null);

        await Bb.ProblemAsync(response, HttpStatusCode.PreconditionRequired, "Visit.PreconditionRequired");
        var after = await Bb.VisitAsync(client, Bb.Id(visit));
        Assert.That(Bb.Version(after), Is.EqualTo(Bb.Version(visit)));
        Assert.That(Bb.Status(after), Is.EqualTo(Bb.Status(visit)));
    }

    [TestCase(-1)]
    [TestCase(1)]
    [TestCase(100)]
    public async Task Inna_wersja_w_If_Match_daje_409_z_currentVersion_i_currentStatus(int offset)
    {
        var client = await Api.ClientAsync(_factory);
        var visit = await Bb.StartPlannedAsync(client);
        var current = Bb.Version(visit);

        var response = await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{Bb.Id(visit)}/activities",
            new { code = "DIAG", description = "Stara wersja" }, current + offset);

        var problem = await Bb.ProblemAsync(response, HttpStatusCode.Conflict, "Visit.VersionConflict");
        Assert.That(Bb.Int(problem, "currentVersion"), Is.EqualTo(current));
        Assert.That(Bb.Str(problem, "currentStatus"), Is.EqualTo("InProgress"));
        var after = await Bb.VisitAsync(client, Bb.Id(visit));
        Assert.That(Bb.Version(after), Is.EqualTo(current));
        Assert.That(Bb.Items(after.GetProperty("activities")), Is.Empty);
    }

    [Test]
    public async Task ETag_szczegolow_to_wersja_i_dziala_jako_If_Match()
    {
        var client = await Api.ClientAsync(_factory);
        var planned = await Bb.FirstWithStatusAsync(client, "Planned");

        var get = await client.GetAsync($"api/visits/{Bb.Id(planned)}");
        var details = await Bb.OkAsync(get);
        var etag = get.Headers.ETag ?? throw new AssertionException("Szczegóły wizyty bez ETag");
        Assert.That(etag.Tag, Is.EqualTo($"\"{Bb.Version(details)}\""));

        // Nagłówek ETag przepisany 1:1 do If-Match
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/visits/{Bb.Id(planned)}/start") { Content = System.Net.Http.Json.JsonContent.Create(new { }) };
        request.Headers.TryAddWithoutValidation("If-Match", etag.ToString());
        var started = await Bb.OkAsync(await client.SendAsync(request));
        Assert.That(Bb.Version(started), Is.EqualTo(Bb.Version(details) + 1));
    }

    [Test]
    public async Task Kazda_udana_komenda_podnosi_wersje_o_1()
    {
        var client = await Api.ClientAsync(_factory);
        var partId = await Bb.AnyPartIdAsync(client);
        var planned = await Bb.FirstWithStatusAsync(client, "Planned");
        var v0 = Bb.Version(planned);

        var started = await Bb.OkCommandAsync(client, HttpMethod.Post, $"api/visits/{Bb.Id(planned)}/start", new { }, v0);
        Assert.That(Bb.Version(started), Is.EqualTo(v0 + 1));
        var withActivity = await Bb.RecordActivityAsync(client, started);
        Assert.That(Bb.Version(withActivity), Is.EqualTo(v0 + 2));
        var withPart = await Bb.UsePartAsync(client, withActivity, partId);
        Assert.That(Bb.Version(withPart), Is.EqualTo(v0 + 3));
        var withoutPart = await Bb.OkCommandAsync(client, HttpMethod.Delete, $"api/visits/{Bb.Id(planned)}/parts/{partId}", null, v0 + 3);
        Assert.That(Bb.Version(withoutPart), Is.EqualTo(v0 + 4));
        var signed = await Bb.SignAsync(client, withoutPart);
        Assert.That(Bb.Version(signed), Is.EqualTo(v0 + 5));
        var completed = await Bb.OkAsync(await Bb.CompleteAsync(client, signed, "Fixed"));
        Assert.That(Bb.Version(completed), Is.EqualTo(v0 + 6));

        // Ta sama wersja w szczegółach, w nagłówku ETag i na liście na dziś
        var get = await client.GetAsync($"api/visits/{Bb.Id(planned)}");
        Assert.That(Bb.Version(await Bb.OkAsync(get)), Is.EqualTo(v0 + 6));
        Assert.That(get.Headers.ETag?.Tag, Is.EqualTo($"\"{v0 + 6}\""));
        var summary = (await Bb.TodayAsync(client)).Single(v => Bb.Id(v) == Bb.Id(planned));
        Assert.That(Bb.Version(summary), Is.EqualTo(v0 + 6));
        Assert.That(Bb.Status(summary), Is.EqualTo("Completed"));
    }

    [Test]
    public async Task Odrzucona_komenda_nie_podnosi_wersji()
    {
        var client = await Api.ClientAsync(_factory);
        var started = await Bb.StartPlannedAsync(client);

        var rejected = await Bb.CompleteAsync(client, started, "Fixed");

        await Bb.ProblemAsync(rejected, (HttpStatusCode)422, "Visit.SignatureRequired");
        Assert.That(Bb.Version(await Bb.VisitAsync(client, Bb.Id(started))), Is.EqualTo(Bb.Version(started)));
    }

    // ---------- R1 ----------

    [Test]
    public async Task R1_start_zaplanowanej_zmienia_status_i_zapisuje_czas_z_telefonu()
    {
        var client = await Api.ClientAsync(_factory);
        var planned = await Bb.FirstWithStatusAsync(client, "Planned");
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-7);

        var started = await Bb.OkCommandAsync(client, HttpMethod.Post, $"api/visits/{Bb.Id(planned)}/start", new { startedAt }, Bb.Version(planned));

        Assert.That(Bb.Status(started), Is.EqualTo("InProgress"));
        Assert.That(Bb.Time(started, "startedAt"), Is.EqualTo(startedAt).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task R1_start_wizyty_w_trakcie_daje_409_InvalidTransition()
    {
        var client = await Api.ClientAsync(_factory);
        var inProgress = await Bb.FirstWithStatusAsync(client, "InProgress");

        var response = await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{Bb.Id(inProgress)}/start", new { }, Bb.Version(inProgress));

        await Bb.ProblemAsync(response, HttpStatusCode.Conflict, "Visit.InvalidTransition");
    }

    [Test]
    public async Task R1_start_zakonczonej_wizyty_daje_409_InvalidTransition()
    {
        var client = await Api.ClientAsync(_factory);
        var inProgress = await Bb.FirstWithStatusAsync(client, "InProgress");
        var completed = await Bb.OkAsync(await Bb.CompleteAsync(client, inProgress, "NotFixed", "Brak dostępu do urządzenia"));

        var response = await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{Bb.Id(completed)}/start", new { }, Bb.Version(completed));

        await Bb.ProblemAsync(response, HttpStatusCode.Conflict, "Visit.InvalidTransition");
    }

    // ---------- R2 ----------

    [TestCase("activities")]
    [TestCase("parts")]
    [TestCase("remove-part")]
    public async Task R2_zmiana_zakresu_zaplanowanej_wizyty_daje_409_NotInProgress(string command)
    {
        var client = await Api.ClientAsync(_factory);
        var planned = await Bb.FirstWithStatusAsync(client, "Planned");
        var (method, url, body) = Bb.Command(command, Bb.Id(planned), await Bb.AnyPartIdAsync(client));

        var response = await Api.CommandAsync(client, method, url, body, Bb.Version(planned));

        await Bb.ProblemAsync(response, HttpStatusCode.Conflict, "Visit.NotInProgress");
    }

    [TestCase("activities")]
    [TestCase("parts")]
    public async Task R2_zmiana_zakresu_zakonczonej_wizyty_daje_409_NotInProgress(string command)
    {
        var client = await Api.ClientAsync(_factory);
        var inProgress = await Bb.FirstWithStatusAsync(client, "InProgress");
        var completed = await Bb.OkAsync(await Bb.CompleteAsync(client, inProgress, "NeedsFollowUp", "Brak części"));
        var (method, url, body) = Bb.Command(command, Bb.Id(completed), await Bb.AnyPartIdAsync(client));

        var response = await Api.CommandAsync(client, method, url, body, Bb.Version(completed));

        await Bb.ProblemAsync(response, HttpStatusCode.Conflict, "Visit.NotInProgress");
    }

    [Test]
    public async Task R2_czynnosc_trafia_do_zakresu_prac()
    {
        var client = await Api.ClientAsync(_factory);
        var started = await Bb.StartPlannedAsync(client);

        var after = await Bb.OkCommandAsync(client, HttpMethod.Post, $"api/visits/{Bb.Id(started)}/activities",
            new { code = "WYM-FILTR", description = "Wymiana filtra siatkowego" }, Bb.Version(started));

        var activity = Bb.Items(after.GetProperty("activities")).Single();
        Assert.That(Bb.Str(activity, "code"), Is.EqualTo("WYM-FILTR"));
        Assert.That(Bb.Str(activity, "description"), Is.EqualTo("Wymiana filtra siatkowego"));
        Assert.That(Bb.IsNullOrMissing(activity, "recordedAt"), Is.False);
    }

    [Test]
    public async Task R2_ta_sama_czesc_drugi_raz_zwieksza_ilosc()
    {
        var client = await Api.ClientAsync(_factory);
        var partId = await Bb.AnyPartIdAsync(client);
        var started = await Bb.StartPlannedAsync(client);

        var once = await Bb.UsePartAsync(client, started, partId, 1);
        var twice = await Bb.UsePartAsync(client, once, partId, 2);

        var part = Bb.Items(twice.GetProperty("parts")).Single();
        Assert.That(Bb.Str(part, "partId"), Is.EqualTo(partId));
        Assert.That(Bb.Int(part, "quantity"), Is.EqualTo(3));
        Assert.That(Bb.Str(part, "partName"), Is.Not.Null.And.Not.Empty);
    }

    [TestCase(0)]
    [TestCase(-1)]
    public async Task R2_ilosc_czesci_musi_byc_dodatnia(int quantity)
    {
        var client = await Api.ClientAsync(_factory);
        var started = await Bb.StartPlannedAsync(client);

        var response = await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{Bb.Id(started)}/parts",
            new { partId = await Bb.AnyPartIdAsync(client), quantity }, Bb.Version(started));

        await Bb.ProblemAsync(response, (HttpStatusCode)422, "Visit.InvalidQuantity");
        var after = await Bb.VisitAsync(client, Bb.Id(started));
        Assert.That(Bb.Items(after.GetProperty("parts")), Is.Empty);
        Assert.That(Bb.Version(after), Is.EqualTo(Bb.Version(started)));
    }

    // ---------- R4 ----------

    [Test]
    public async Task R4_podpis_przy_pustym_zakresie_daje_422_EmptyScope()
    {
        var client = await Api.ClientAsync(_factory);
        var started = await Bb.StartPlannedAsync(client);

        var response = await Api.CommandAsync(client, HttpMethod.Put, $"api/visits/{Bb.Id(started)}/signature",
            new { signerName = "Anna Wiśniewska" }, Bb.Version(started));

        await Bb.ProblemAsync(response, (HttpStatusCode)422, "Visit.EmptyScope");
        Assert.That(Bb.Signature(await Bb.VisitAsync(client, Bb.Id(started))), Is.Null);
    }

    [Test]
    public async Task R4_podpis_zaplanowanej_wizyty_daje_409()
    {
        var client = await Api.ClientAsync(_factory);
        var planned = await Bb.FirstWithStatusAsync(client, "Planned");

        var response = await Api.CommandAsync(client, HttpMethod.Put, $"api/visits/{Bb.Id(planned)}/signature",
            new { signerName = "Anna Wiśniewska" }, Bb.Version(planned));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task R4_podpis_obejmuje_biezacy_zakres()
    {
        var client = await Api.ClientAsync(_factory);
        var withActivity = await Bb.RecordActivityAsync(client, await Bb.StartPlannedAsync(client));

        var signed = await Bb.SignAsync(client, withActivity, "Jan Nowak");

        var signature = Bb.RequiredSignature(signed);
        Assert.That(Bb.Str(signature, "signerName"), Is.EqualTo("Jan Nowak"));
        Assert.That(signature.GetProperty("isValid").GetBoolean(), Is.True);
        Assert.That(Bb.IsNullOrMissing(signature, "signedAt"), Is.False);
        Assert.That(Bb.Int(signature, "scopeVersion"), Is.EqualTo(Bb.Int(signed, "scopeVersion")));
    }

    // ---------- R3 ----------

    [TestCase("activities")]
    [TestCase("parts")]
    public async Task R3_zmiana_zakresu_po_podpisie_uniewaznia_podpis(string command)
    {
        var client = await Api.ClientAsync(_factory);
        var partId = await Bb.AnyPartIdAsync(client);
        var signed = await Bb.SignAsync(client, await Bb.RecordActivityAsync(client, await Bb.StartPlannedAsync(client)));
        var signedScope = Bb.Int(signed, "scopeVersion");
        var (method, url, body) = Bb.Command(command, Bb.Id(signed), partId);

        var changed = await Bb.OkCommandAsync(client, method, url, body, Bb.Version(signed));

        // Podpis zostaje, ale wskazuje starszy zakres
        var signature = Bb.RequiredSignature(changed);
        Assert.That(signature.GetProperty("isValid").GetBoolean(), Is.False);
        Assert.That(Bb.Int(signature, "scopeVersion"), Is.EqualTo(signedScope));
        Assert.That(Bb.Int(changed, "scopeVersion"), Is.GreaterThan(signedScope));

        // Fixed z nieważnym podpisem – odmowa
        await Bb.ProblemAsync(await Bb.CompleteAsync(client, changed, "Fixed"), (HttpStatusCode)422, "Visit.SignatureRequired");
    }

    [Test]
    public async Task R3_ponowny_podpis_po_zmianie_zakresu_pozwala_zakonczyc_Fixed()
    {
        var client = await Api.ClientAsync(_factory);
        var signed = await Bb.SignAsync(client, await Bb.RecordActivityAsync(client, await Bb.StartPlannedAsync(client)));
        var changed = await Bb.UsePartAsync(client, signed, await Bb.AnyPartIdAsync(client));

        var resigned = await Bb.SignAsync(client, changed);

        Assert.That(Bb.RequiredSignature(resigned).GetProperty("isValid").GetBoolean(), Is.True);
        var completed = await Bb.OkAsync(await Bb.CompleteAsync(client, resigned, "Fixed"));
        Assert.That(Bb.Status(completed), Is.EqualTo("Completed"));
    }

    // ---------- R7 ----------

    [Test]
    public async Task R7_usuniecie_czesci_spoza_zakresu_daje_422_PartNotInScope()
    {
        var client = await Api.ClientAsync(_factory);
        var started = await Bb.UsePartAsync(client, await Bb.StartPlannedAsync(client), await Bb.AnyPartIdAsync(client, 0));

        var response = await Api.CommandAsync(client, HttpMethod.Delete,
            $"api/visits/{Bb.Id(started)}/parts/{await Bb.AnyPartIdAsync(client, 1)}", null, Bb.Version(started));

        await Bb.ProblemAsync(response, (HttpStatusCode)422, "Visit.PartNotInScope");
    }

    [Test]
    public async Task R7_usuniecie_omylkowej_czesci_usuwa_ja_z_zakresu()
    {
        var client = await Api.ClientAsync(_factory);
        var keep = await Bb.AnyPartIdAsync(client, 0);
        var mistake = await Bb.AnyPartIdAsync(client, 1);
        var visit = await Bb.UsePartAsync(client, await Bb.StartPlannedAsync(client), keep);
        visit = await Bb.UsePartAsync(client, visit, mistake, 2);

        var after = await Bb.OkCommandAsync(client, HttpMethod.Delete, $"api/visits/{Bb.Id(visit)}/parts/{mistake}", null, Bb.Version(visit));

        Assert.That(Bb.Items(after.GetProperty("parts")).Select(p => Bb.Str(p, "partId")), Is.EqualTo(new[] { keep }));
        Assert.That(Bb.Version(after), Is.EqualTo(Bb.Version(visit) + 1));
    }

    [Test]
    public async Task R7_usuniecie_czesci_po_podpisie_uniewaznia_podpis()
    {
        var client = await Api.ClientAsync(_factory);
        var partId = await Bb.AnyPartIdAsync(client);
        var visit = await Bb.RecordActivityAsync(client, await Bb.StartPlannedAsync(client));
        var signed = await Bb.SignAsync(client, await Bb.UsePartAsync(client, visit, partId));

        var after = await Bb.OkCommandAsync(client, HttpMethod.Delete, $"api/visits/{Bb.Id(signed)}/parts/{partId}", null, Bb.Version(signed));

        Assert.That(Bb.RequiredSignature(after).GetProperty("isValid").GetBoolean(), Is.False);
    }

    // ---------- R5 ----------

    [Test]
    public async Task R5_Fixed_bez_podpisu_daje_422_SignatureRequired()
    {
        var client = await Api.ClientAsync(_factory);
        var withScope = await Bb.RecordActivityAsync(client, await Bb.StartPlannedAsync(client));

        var response = await Bb.CompleteAsync(client, withScope, "Fixed");

        await Bb.ProblemAsync(response, (HttpStatusCode)422, "Visit.SignatureRequired");
        Assert.That(Bb.Status(await Bb.VisitAsync(client, Bb.Id(withScope))), Is.EqualTo("InProgress"));
    }

    [Test]
    public async Task R5_Fixed_z_waznym_podpisem_konczy_wizyte_i_zamyka_zlecenie()
    {
        var client = await Api.ClientAsync(_factory);
        var signed = await Bb.SignAsync(client, await Bb.RecordActivityAsync(client, await Bb.StartPlannedAsync(client)));
        var workOrderId = signed.GetProperty("workOrderId").GetGuid();

        var completed = await Bb.OkAsync(await Bb.CompleteAsync(client, signed, "Fixed"));

        Assert.That(Bb.Status(completed), Is.EqualTo("Completed"));
        Assert.That(Bb.Str(completed, "outcome"), Is.EqualTo("Fixed"));
        Assert.That(Bb.IsNullOrMissing(completed, "completedAt"), Is.False);
        var order = Bb.Items(await Bb.GetOkAsync(client, "api/workorders")).Single(o => Bb.Id(o) == workOrderId);
        Assert.That(Bb.Status(order), Is.EqualTo("Closed"));
    }

    [TestCase("NeedsFollowUp", null)]
    [TestCase("NeedsFollowUp", "")]
    [TestCase("NotFixed", null)]
    [TestCase("NotFixed", "")]
    public async Task R5_wynik_inny_niz_Fixed_bez_powodu_daje_422(string outcome, string? reason)
    {
        var client = await Api.ClientAsync(_factory);
        var inProgress = await Bb.FirstWithStatusAsync(client, "InProgress");

        var response = await Bb.CompleteAsync(client, inProgress, outcome, reason);

        await Bb.ProblemAsync(response, (HttpStatusCode)422, "Visit.ReasonRequired");
        Assert.That(Bb.Status(await Bb.VisitAsync(client, Bb.Id(inProgress))), Is.EqualTo("InProgress"));
    }

    [TestCase("NeedsFollowUp", "Brak sprężarki na stanie")]
    [TestCase("NotFixed", "Urządzenie do wymiany")]
    public async Task R5_wynik_z_powodem_konczy_wizyte_a_zlecenie_zostaje_otwarte(string outcome, string reason)
    {
        var client = await Api.ClientAsync(_factory);
        var inProgress = await Bb.FirstWithStatusAsync(client, "InProgress");

        var completed = await Bb.OkAsync(await Bb.CompleteAsync(client, inProgress, outcome, reason));

        Assert.That(Bb.Status(completed), Is.EqualTo("Completed"));
        Assert.That(Bb.Str(completed, "outcome"), Is.EqualTo(outcome));
        Assert.That(Bb.Str(completed, "outcomeReason"), Is.EqualTo(reason));
        var order = Bb.Items(await Bb.GetOkAsync(client, "api/workorders")).Single(o => Bb.Id(o) == completed.GetProperty("workOrderId").GetGuid());
        Assert.That(Bb.Status(order), Is.EqualTo("Open"));
    }

    [Test]
    public async Task R5_zakonczenie_zaplanowanej_wizyty_daje_409()
    {
        var client = await Api.ClientAsync(_factory);
        var planned = await Bb.FirstWithStatusAsync(client, "Planned");

        var response = await Bb.CompleteAsync(client, planned, "NotFixed", "Klient nieobecny");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task R5_ponowne_zakonczenie_daje_409()
    {
        var client = await Api.ClientAsync(_factory);
        var inProgress = await Bb.FirstWithStatusAsync(client, "InProgress");
        var completed = await Bb.OkAsync(await Bb.CompleteAsync(client, inProgress, "NotFixed", "Brak zasilania"));

        var again = await Bb.CompleteAsync(client, completed, "NotFixed", "Brak zasilania");

        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), await again.Content.ReadAsStringAsync());
    }

    // ---------- Cudze i nieistniejące wizyty ----------

    [TestCaseSource(nameof(AllCommands))]
    public async Task Komenda_na_wizycie_innego_technika_daje_404(string command)
    {
        var owner = await Api.ClientAsync(_factory);
        var stranger = await Api.ClientAsync(_factory);
        var visit = command == "start" ? await Bb.FirstWithStatusAsync(owner, "Planned") : await Bb.StartPlannedAsync(owner);
        var (method, url, body) = Bb.Command(command, Bb.Id(visit), await Bb.AnyPartIdAsync(owner));

        var response = await Api.CommandAsync(stranger, method, url, body, Bb.Version(visit));

        await Bb.ProblemAsync(response, HttpStatusCode.NotFound, "Visit.NotFound");
        Assert.That(Bb.Version(await Bb.VisitAsync(owner, Bb.Id(visit))), Is.EqualTo(Bb.Version(visit)));
    }

    [Test]
    public async Task Szczegoly_wizyty_innego_technika_i_nieistniejacej_daja_404()
    {
        var owner = await Api.ClientAsync(_factory);
        var stranger = await Api.ClientAsync(_factory);
        var visit = (await Bb.TodayAsync(owner))[0];

        await Bb.ProblemAsync(await stranger.GetAsync($"api/visits/{Bb.Id(visit)}"), HttpStatusCode.NotFound, "Visit.NotFound");
        await Bb.ProblemAsync(await owner.GetAsync($"api/visits/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "Visit.NotFound");
    }

    // ---------- Idempotency-Key ----------

    [Test]
    public async Task Powtorka_z_tym_samym_kluczem_zwraca_zapisana_odpowiedz_bez_drugiego_wykonania()
    {
        var client = await Api.ClientAsync(_factory);
        var started = await Bb.StartPlannedAsync(client);
        var url = $"api/visits/{Bb.Id(started)}/activities";
        var body = new { code = "DIAG", description = "Pomiar ciśnienia" };
        var key = Guid.NewGuid().ToString();

        var first = await Api.CommandAsync(client, HttpMethod.Post, url, body, Bb.Version(started), key);
        var firstText = await first.Content.ReadAsStringAsync();
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK), firstText);
        Assert.That(Bb.Header(first, "Idempotent-Replayed"), Is.Null.Or.EqualTo("false"));

        var replay = await Api.CommandAsync(client, HttpMethod.Post, url, body, Bb.Version(started), key);
        var replayText = await replay.Content.ReadAsStringAsync();

        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.OK), replayText);
        Assert.That(replayText, Is.EqualTo(firstText));
        Assert.That(Bb.Header(replay, "Idempotent-Replayed"), Is.EqualTo("true").IgnoreCase);
        var after = await Bb.VisitAsync(client, Bb.Id(started));
        Assert.That(Bb.Version(after), Is.EqualTo(Bb.Version(started) + 1));
        Assert.That(Bb.Items(after.GetProperty("activities")), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Powtorka_dziala_takze_gdy_wersja_wizyty_jest_juz_nowsza()
    {
        var client = await Api.ClientAsync(_factory);
        var started = await Bb.StartPlannedAsync(client);
        var url = $"api/visits/{Bb.Id(started)}/activities";
        var body = new { code = "DIAG", description = "Z outboxa" };
        var key = Guid.NewGuid().ToString();
        var first = await Bb.OkAsync(await Api.CommandAsync(client, HttpMethod.Post, url, body, Bb.Version(started), key));
        await Bb.RecordActivityAsync(client, first, "Kolejna czynność");

        var replay = await Api.CommandAsync(client, HttpMethod.Post, url, body, Bb.Version(started), key);

        var replayed = await Bb.OkAsync(replay);
        Assert.That(Bb.Header(replay, "Idempotent-Replayed"), Is.EqualTo("true").IgnoreCase);
        Assert.That(Bb.Version(replayed), Is.EqualTo(Bb.Version(first)), "Zapisana odpowiedź, nie bieżący stan");
        var after = await Bb.VisitAsync(client, Bb.Id(started));
        Assert.That(Bb.Items(after.GetProperty("activities")), Has.Count.EqualTo(2));
        Assert.That(Bb.Version(after), Is.EqualTo(Bb.Version(started) + 2));
    }

    [Test]
    public async Task Ten_sam_klucz_z_inna_trescia_daje_422_KeyReused()
    {
        var client = await Api.ClientAsync(_factory);
        var started = await Bb.StartPlannedAsync(client);
        var url = $"api/visits/{Bb.Id(started)}/activities";
        var key = Guid.NewGuid().ToString();
        var first = await Bb.OkAsync(await Api.CommandAsync(client, HttpMethod.Post, url,
            new { code = "DIAG", description = "Pierwsza treść" }, Bb.Version(started), key));

        var reused = await Api.CommandAsync(client, HttpMethod.Post, url,
            new { code = "DIAG", description = "Inna treść" }, Bb.Version(first), key);

        await Bb.ProblemAsync(reused, (HttpStatusCode)422, "Idempotency.KeyReused");
        var after = await Bb.VisitAsync(client, Bb.Id(started));
        Assert.That(Bb.Items(after.GetProperty("activities")), Has.Count.EqualTo(1));
        Assert.That(Bb.Version(after), Is.EqualTo(Bb.Version(first)));
    }

    [Test]
    public async Task Rozne_klucze_to_rozne_komendy()
    {
        var client = await Api.ClientAsync(_factory);
        var started = await Bb.StartPlannedAsync(client);
        var url = $"api/visits/{Bb.Id(started)}/activities";
        var body = new { code = "DIAG", description = "Ta sama treść" };

        var first = await Bb.OkAsync(await Api.CommandAsync(client, HttpMethod.Post, url, body, Bb.Version(started), Guid.NewGuid().ToString()));
        var second = await Api.CommandAsync(client, HttpMethod.Post, url, body, Bb.Version(first), Guid.NewGuid().ToString());

        Assert.That(Bb.Header(second, "Idempotent-Replayed"), Is.Null.Or.EqualTo("false"));
        Assert.That(Bb.Items((await Bb.OkAsync(second)).GetProperty("activities")), Has.Count.EqualTo(2));
    }
}
