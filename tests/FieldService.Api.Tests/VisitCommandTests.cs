using System.Net;
using System.Net.Http.Json;
using FieldService.Api.Contracts;
using FieldService.Api.Domain;

namespace FieldService.Api.Tests;

public sealed class VisitCommandTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    /// <summary>Pierwsza wizyta dnia jest rozpoczęta (InProgress), druga zaplanowana.</summary>
    private async Task<(HttpClient Client, VisitSummaryDto InProgress, VisitSummaryDto Planned)> DayAsync()
    {
        var client = await Api.ClientAsync(_factory);
        var today = await Api.TodayAsync(client);
        return (client, today.Single(v => v.Status == VisitStatus.InProgress), today.First(v => v.Status == VisitStatus.Planned));
    }

    [Test]
    public async Task Command_without_If_Match_returns_428()
    {
        var (client, _, planned) = await DayAsync();

        var response = await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{planned.Id}/start", null, version: null);

        Assert.That(response.StatusCode, Is.EqualTo((HttpStatusCode)428));
        Assert.That(await Api.ErrorCodeAsync(response), Is.EqualTo("Visit.PreconditionRequired"));
    }

    // Proxy po drodze może wyciąć If-Match – ta sama wartość w X-If-Match działa tak samo
    [Test]
    public async Task Command_with_X_If_Match_uses_that_version()
    {
        var (client, _, planned) = await DayAsync();

        var started = await SendWithXIfMatch(client, $"api/visits/{planned.Id}/start", planned.Version);
        var stale = await SendWithXIfMatch(client, $"api/visits/{planned.Id}/start", planned.Version);

        Assert.That(started.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    private static Task<HttpResponseMessage> SendWithXIfMatch(HttpClient client, string url, int version)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("X-If-Match", $"\"{version}\"");
        return client.SendAsync(request);
    }

    [Test]
    public async Task Command_with_old_version_returns_409_with_current_version()
    {
        var (client, _, planned) = await DayAsync();
        await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{planned.Id}/start", null, planned.Version);

        var response = await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{planned.Id}/activities",
            new RecordActivityRequest { Code = "DIAG", Description = "Diagnostyka" }, planned.Version);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await Api.ErrorCodeAsync(response), Is.EqualTo("Visit.VersionConflict"));
        Assert.That(response.Headers.ETag?.Tag, Is.EqualTo($"\"{planned.Version + 1}\""));
    }

    [Test]
    public async Task Start_twice_returns_InvalidTransition()
    {
        var (client, inProgress, _) = await DayAsync();

        var response = await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{inProgress.Id}/start", null, inProgress.Version);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await Api.ErrorCodeAsync(response), Is.EqualTo("Visit.InvalidTransition"));
    }

    [Test]
    public async Task Repeated_command_with_same_key_returns_saved_response_without_second_execution()
    {
        var (client, inProgress, _) = await DayAsync();
        var body = new UsePartRequest { PartId = "FILTR-SIATKOWY-22", Quantity = 1 };

        var first = await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{inProgress.Id}/parts", body, inProgress.Version, "klucz-1");
        // ponowienie po zerwanym połączeniu: ten sam klucz i ta sama (już nieaktualna) wersja
        var repeated = await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{inProgress.Id}/parts", body, inProgress.Version, "klucz-1");

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(repeated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(repeated.Headers.GetValues("Idempotent-Replayed"), Is.EqualTo(new[] { "true" }));
        var visit = (await client.GetFromJsonAsync<VisitDetailsDto>($"api/visits/{inProgress.Id}", Api.Json))!;
        Assert.That(visit.Parts.Single().Quantity, Is.EqualTo(1));
        Assert.That(visit.Version, Is.EqualTo(inProgress.Version + 1));
    }

    [Test]
    public async Task Same_key_with_different_body_returns_422_KeyReused()
    {
        var (client, inProgress, _) = await DayAsync();
        await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{inProgress.Id}/parts",
            new UsePartRequest { PartId = "FILTR-SIATKOWY-22", Quantity = 1 }, inProgress.Version, "klucz-2");

        var response = await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{inProgress.Id}/parts",
            new UsePartRequest { PartId = "FILTR-SIATKOWY-22", Quantity = 5 }, inProgress.Version + 1, "klucz-2");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
        Assert.That(await Api.ErrorCodeAsync(response), Is.EqualTo("Idempotency.KeyReused"));
    }

    [Test]
    public async Task Scope_change_after_signature_invalidates_signature_and_Fixed_needs_new_one()
    {
        var (client, visit, _) = await DayAsync();
        var url = $"api/visits/{visit.Id}";
        var v = visit.Version;

        Assert.That((await Api.CommandAsync(client, HttpMethod.Put, $"{url}/signature", new SignVisitRequest { SignerName = "Anna Wiśniewska" }, v++)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var afterPart = await Api.ReadAsync<VisitDetailsDto>(
            await Api.CommandAsync(client, HttpMethod.Post, $"{url}/parts", new UsePartRequest { PartId = "CZUJNIK-NTC-10K", Quantity = 1 }, v++));
        var fixedWithoutSignature = await Api.CommandAsync(client, HttpMethod.Post, $"{url}/complete", new CompleteVisitRequest { Outcome = VisitOutcomeType.Fixed }, v);

        Assert.That(afterPart.Signature!.IsValid, Is.False);
        Assert.That(fixedWithoutSignature.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
        Assert.That(await Api.ErrorCodeAsync(fixedWithoutSignature), Is.EqualTo("Visit.SignatureRequired"));

        await Api.CommandAsync(client, HttpMethod.Put, $"{url}/signature", new SignVisitRequest { SignerName = "Anna Wiśniewska" }, v++);
        var completed = await Api.ReadAsync<VisitDetailsDto>(
            await Api.CommandAsync(client, HttpMethod.Post, $"{url}/complete", new CompleteVisitRequest { Outcome = VisitOutcomeType.Fixed }, v));
        var orders = (await client.GetFromJsonAsync<List<WorkOrderSummaryDto>>("api/workorders", Api.Json))!;

        Assert.That(completed.Status, Is.EqualTo(VisitStatus.Completed));
        Assert.That(orders.Single(o => o.Id == completed.WorkOrderId).Status, Is.EqualTo(WorkOrderStatus.Closed));
    }

    [Test]
    public async Task Signature_on_empty_scope_returns_EmptyScope()
    {
        var (client, _, planned) = await DayAsync();
        await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{planned.Id}/start", null, planned.Version);

        var response = await Api.CommandAsync(client, HttpMethod.Put, $"api/visits/{planned.Id}/signature",
            new SignVisitRequest { SignerName = "Jan" }, planned.Version + 1);

        Assert.That(await Api.ErrorCodeAsync(response), Is.EqualTo("Visit.EmptyScope"));
    }

    [Test]
    public async Task Remove_used_part_not_in_scope_returns_PartNotInScope()
    {
        var (client, visit, _) = await DayAsync();

        var response = await Api.CommandAsync(client, HttpMethod.Delete, $"api/visits/{visit.Id}/parts/ZAWOR-4D", null, visit.Version);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
        Assert.That(await Api.ErrorCodeAsync(response), Is.EqualTo("Visit.PartNotInScope"));
    }

    [Test]
    public async Task NotFixed_without_reason_returns_ReasonRequired()
    {
        var (client, visit, _) = await DayAsync();

        var response = await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{visit.Id}/complete",
            new CompleteVisitRequest { Outcome = VisitOutcomeType.NotFixed }, visit.Version);

        Assert.That(await Api.ErrorCodeAsync(response), Is.EqualTo("Visit.ReasonRequired"));
    }

    [Test]
    public async Task Dispatcher_cancel_bumps_version_so_phone_command_gets_conflict()
    {
        var (client, visit, _) = await DayAsync();
        var dispatcher = _factory.CreateClient();

        var cancelled = await dispatcher.PostAsJsonAsync($"api/dispatcher/visits/{visit.Id}/cancel", new CancelVisitRequest { Reason = "Klient odwołał" });
        var fromPhone = await Api.CommandAsync(client, HttpMethod.Post, $"api/visits/{visit.Id}/complete",
            new CompleteVisitRequest { Outcome = VisitOutcomeType.NotFixed, Reason = "x" }, visit.Version);

        Assert.That(cancelled.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await Api.ErrorCodeAsync(fromPhone), Is.EqualTo("Visit.VersionConflict"));
    }

    [Test]
    public async Task Visit_of_other_technician_returns_404()
    {
        var (_, visit, _) = await DayAsync();
        var other = await Api.ClientAsync(_factory);

        var response = await other.GetAsync($"api/visits/{visit.Id}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
