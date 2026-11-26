using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace FieldService.Api.Tests.BlackBox;

/// <summary>Logowanie (e-mail + PIN 1234), rotacja refresh tokenu i ochrona endpointów tokenem.</summary>
public class AuthBlackBoxTests
{
    private ApiFactory _factory = null!;

    [OneTimeSetUp] public void SetUp() => _factory = new ApiFactory();
    [OneTimeTearDown] public void TearDown() => _factory.Dispose();

    private const string AnyId = "3f2b8c1e-5a4d-4c2b-9e7f-0123456789ab";

    /// <summary>Wszystkie endpointy z kontraktu, które wymagają tokenu (security: Bearer w openapi.json).</summary>
    private static readonly object[] ProtectedEndpoints =
    [
        new object[] { "GET", "api/visits/today" },
        new object[] { "GET", "api/visits/history" },
        new object[] { "GET", $"api/visits/{AnyId}" },
        new object[] { "POST", $"api/visits/{AnyId}/start" },
        new object[] { "POST", $"api/visits/{AnyId}/activities" },
        new object[] { "POST", $"api/visits/{AnyId}/parts" },
        new object[] { "DELETE", $"api/visits/{AnyId}/parts/FILTR-SIATKOWY-22" },
        new object[] { "PUT", $"api/visits/{AnyId}/signature" },
        new object[] { "POST", $"api/visits/{AnyId}/complete" },
        new object[] { "GET", "api/workorders" },
        new object[] { "GET", "api/parts" },
        new object[] { "GET", "api/warehouses" },
        new object[] { "GET", "api/parts/KOMPRESOR-R32/stock" },
        new object[] { "GET", "api/vanstock" },
        new object[] { "POST", "api/parts/reservations" },
        new object[] { "GET", "api/parts/reservations" },
        new object[] { "DELETE", $"api/parts/reservations/{AnyId}" }
    ];

    private static HttpRequestMessage Request(string method, string url)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT") request.Content = JsonContent.Create(new { });
        return request;
    }

    [Test]
    public async Task Poprawny_email_i_PIN_zwracaja_pare_tokenow()
    {
        var client = _factory.CreateClient();

        var tokens = await Bb.LoginJsonAsync(client, Api.NewEmail());

        Assert.That(Bb.Str(tokens, "accessToken"), Is.Not.Null.And.Not.Empty);
        Assert.That(Bb.Str(tokens, "refreshToken"), Is.Not.Null.And.Not.Empty);
        Assert.That(Bb.Int(tokens, "expiresIn"), Is.GreaterThan(0));
        Assert.That(Bb.Str(tokens, "technicianName"), Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Access_token_z_logowania_otwiera_chroniony_endpoint()
    {
        var client = _factory.CreateClient();
        var tokens = await Bb.LoginJsonAsync(client, Api.NewEmail());
        Bb.UseToken(client, Bb.Str(tokens, "accessToken"));

        var response = await client.GetAsync("api/visits/today");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [TestCase("1111")]
    [TestCase("12345")]
    [TestCase("")]
    public async Task Niepoprawny_PIN_daje_401(string pin)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("api/auth/login", new { email = Api.NewEmail(), pin });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [TestCase("bez-malpy")]
    [TestCase("jan@")]
    [TestCase("")]
    public async Task Niepoprawny_email_daje_401(string email)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("api/auth/login", new { email, pin = "1234" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Refresh_daje_nowa_pare_a_uzyty_refresh_token_jest_odrzucany()
    {
        var client = _factory.CreateClient();
        var first = Bb.Str(await Bb.LoginJsonAsync(client, Api.NewEmail()), "refreshToken");

        // Pierwsze odświeżenie: nowa para, nowy refresh token
        var second = await Bb.OkAsync(await client.PostAsJsonAsync("api/auth/refresh", new { refreshToken = first }));
        var secondRefresh = Bb.Str(second, "refreshToken");
        Assert.That(secondRefresh, Is.Not.Null.And.Not.Empty.And.Not.EqualTo(first));
        Assert.That(Bb.Str(second, "accessToken"), Is.Not.Null.And.Not.Empty);

        // Nowy refresh token działa (zanim spróbujemy starego – na wypadek unieważniania całej rodziny tokenów)
        var third = await Bb.OkAsync(await client.PostAsJsonAsync("api/auth/refresh", new { refreshToken = secondRefresh }));
        Assert.That(Bb.Str(third, "refreshToken"), Is.Not.EqualTo(secondRefresh));

        // Już użyte refresh tokeny – 401 (rotacja: każdy jednorazowy)
        var replayFirst = await client.PostAsJsonAsync("api/auth/refresh", new { refreshToken = first });
        Assert.That(replayFirst.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        var replaySecond = await client.PostAsJsonAsync("api/auth/refresh", new { refreshToken = secondRefresh });
        Assert.That(replaySecond.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Access_token_z_refresh_dziala_dla_tego_samego_technika()
    {
        var email = Api.NewEmail();
        var client = _factory.CreateClient();
        var login = await Bb.LoginJsonAsync(client, email);
        Bb.UseToken(client, Bb.Str(login, "accessToken"));
        var idsBefore = (await Bb.TodayAsync(client)).Select(Bb.Id).ToList();

        var refreshed = await Bb.OkAsync(await client.PostAsJsonAsync("api/auth/refresh", new { refreshToken = Bb.Str(login, "refreshToken") }));
        var other = _factory.CreateClient();
        Bb.UseToken(other, Bb.Str(refreshed, "accessToken"));

        var idsAfter = (await Bb.TodayAsync(other)).Select(Bb.Id).ToList();
        Assert.That(idsAfter, Is.EquivalentTo(idsBefore));
    }

    [TestCase("nieznany-refresh-token")]
    [TestCase("")]
    public async Task Nieznany_refresh_token_daje_401(string refreshToken)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("api/auth/refresh", new { refreshToken });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [TestCaseSource(nameof(ProtectedEndpoints))]
    public async Task Chroniony_endpoint_bez_tokenu_daje_401(string method, string url)
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(Request(method, url));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), $"{method} {url}");
    }

    [TestCaseSource(nameof(ProtectedEndpoints))]
    public async Task Chroniony_endpoint_z_falszywym_tokenem_daje_401(string method, string url)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "to-nie-jest-jwt");

        var response = await client.SendAsync(Request(method, url));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), $"{method} {url}");
    }

    [TestCase("api/health")]
    [TestCase("api/images/pompa-ciepla")]
    [TestCase("api/dispatcher/visits")]
    public async Task Endpointy_publiczne_dzialaja_bez_tokenu(string url)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(url);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
    }
}
