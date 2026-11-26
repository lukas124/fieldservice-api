using System.Net;
using System.Text.Json;

namespace FieldService.Api.Tests.BlackBox;

/// <summary>
/// Brygada: technik@szkolenie.pl i technik2@szkolenie.pl mają wspólną wizytę 12:30 (dwa telefony, konflikt wersji).
/// </summary>
public class BrigadeBlackBoxTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    private static async Task<JsonElement> BrigadeVisitAsync(HttpClient client)
    {
        var brigade = (await Bb.TodayAsync(client)).Where(v => v.GetProperty("isBrigade").GetBoolean()).ToList();
        Assert.That(brigade, Has.Count.EqualTo(1), "Dokładnie jedna wizyta brygady na dziś");
        return brigade[0];
    }

    [Test]
    public async Task Brygada_ma_jedna_wspolna_wizyte_12_30_z_dwoma_technikami()
    {
        // Kolejne logowania obu techników nie tworzą drugiej wspólnej wizyty
        var first = await Api.ClientAsync(_factory, Bb.BrigadeEmail1);
        var second = await Api.ClientAsync(_factory, Bb.BrigadeEmail2);
        first = await Api.ClientAsync(_factory, Bb.BrigadeEmail1);

        var visitOfFirst = await BrigadeVisitAsync(first);
        var visitOfSecond = await BrigadeVisitAsync(second);

        Assert.That(Bb.Id(visitOfSecond), Is.EqualTo(Bb.Id(visitOfFirst)));
        Assert.That(Bb.ToWarsaw(Bb.Time(visitOfFirst, "plannedStart")).ToString("HH:mm"), Is.EqualTo("12:30"));
        Assert.That(await Bb.TodayAsync(first), Has.Count.EqualTo(4));
        Assert.That(await Bb.TodayAsync(second), Has.Count.EqualTo(4));

        var details = await Bb.VisitAsync(second, Bb.Id(visitOfFirst));
        var emails = Bb.Items(details.GetProperty("technicianEmails")).Select(e => e.GetString()!.ToLowerInvariant()).ToList();
        Assert.That(emails, Has.Count.EqualTo(2));
        Assert.That(emails, Is.EquivalentTo(new[] { Bb.BrigadeEmail1, Bb.BrigadeEmail2 }));
        Assert.That(Bb.Items(details.GetProperty("technicianNames")), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Dyspozytor_widzi_wspolna_wizyte_u_obu_technikow()
    {
        var first = await Api.ClientAsync(_factory, Bb.BrigadeEmail1);
        await Api.ClientAsync(_factory, Bb.BrigadeEmail2);
        var shared = Bb.Id(await BrigadeVisitAsync(first));

        foreach (var email in new[] { Bb.BrigadeEmail1, Bb.BrigadeEmail2 })
        {
            var list = Bb.Items(await Bb.GetOkAsync(_factory.CreateClient(), $"api/dispatcher/visits?technicianEmail={Uri.EscapeDataString(email)}"));
            var visit = list.FirstOrDefault(v => Bb.Id(v) == shared);
            Assert.That(visit.ValueKind, Is.EqualTo(JsonValueKind.Object), email);
            Assert.That(Bb.Items(visit.GetProperty("technicianEmails")), Has.Count.EqualTo(2));
        }
    }

    [Test]
    public async Task Dwa_telefony_brygady_druga_komenda_ze_stara_wersja_dostaje_409()
    {
        var first = await Api.ClientAsync(_factory, Bb.BrigadeEmail1);
        var second = await Api.ClientAsync(_factory, Bb.BrigadeEmail2);
        var shared = await Bb.VisitAsync(first, Bb.Id(await BrigadeVisitAsync(first)));
        Assume.That(Bb.Status(shared), Is.EqualTo("Planned"), "Wspólna wizyta powinna być jeszcze zaplanowana");
        var seen = Bb.Version(shared);

        // Oba telefony widzą tę samą wersję; pierwszy rozpoczyna wizytę
        var started = await Bb.OkCommandAsync(first, HttpMethod.Post, $"api/visits/{Bb.Id(shared)}/start", new { }, seen);
        Assert.That(Bb.Version(started), Is.EqualTo(seen + 1));

        // Drugi wysyła komendę ze starą wersją → konflikt z aktualną wersją i statusem
        var conflict = await Api.CommandAsync(second, HttpMethod.Post, $"api/visits/{Bb.Id(shared)}/start", new { }, seen);
        var problem = await Bb.ProblemAsync(conflict, HttpStatusCode.Conflict, "Visit.VersionConflict");
        Assert.That(Bb.Int(problem, "currentVersion"), Is.EqualTo(seen + 1));
        Assert.That(Bb.Str(problem, "currentStatus"), Is.EqualTo("InProgress"));

        // Po odświeżeniu drugi telefon może pracować na aktualnej wersji
        var fresh = await Bb.VisitAsync(second, Bb.Id(shared));
        var afterActivity = await Bb.RecordActivityAsync(second, fresh, "Druga osoba z brygady");
        Assert.That(Bb.Version(afterActivity), Is.EqualTo(seen + 2));
    }
}
