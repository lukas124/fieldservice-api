using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using FieldService.Api.Auth;
using FieldService.Api.Controllers;
using FieldService.Api.Data;
using FieldService.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ---------- nasłuch ----------
// Domyślnie http://localhost:5090 (emulator Android: http://10.0.2.2:5090).
// Hosting:ListenOnAllInterfaces=true → 0.0.0.0 (uczestnicy w sieci sali łączą się z laptopem trenera).
// Jawne --urls / ASPNETCORE_URLS / Kestrel:Endpoints mają pierwszeństwo.
var hosting = builder.Configuration.GetSection(HostingOptions.Section).Get<HostingOptions>() ?? new HostingOptions();
var explicitUrls = !string.IsNullOrEmpty(builder.Configuration["urls"])
                   || !string.IsNullOrEmpty(builder.Configuration["http_ports"])
                   || builder.Configuration.GetSection("Kestrel:Endpoints").Exists();
if (!explicitUrls)
{
    builder.WebHost.UseUrls(hosting.ListenOnAllInterfaces ? $"http://0.0.0.0:{hosting.Port}" : $"http://localhost:{hosting.Port}");
}

// ---------- opcje ----------
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection(DemoOptions.Section));
builder.Services.Configure<InventoryOptions>(builder.Configuration.GetSection(InventoryOptions.Section));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.Section));
builder.Services.Configure<DispatcherOptions>(builder.Configuration.GetSection(DispatcherOptions.Section));
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection(RateLimitOptions.Section));

// ---------- usługi ----------
builder.Services.AddSingleton(TimeProvider.System);
// baza: connection string tylko z konfiguracji środowiska (ConnectionStrings__FieldService)
var connectionString = builder.Configuration.GetConnectionString("FieldService");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Brak ConnectionStrings:FieldService (zmienna ConnectionStrings__FieldService).");
builder.Services.AddDbContext<FieldServiceDbContext>(o => o.UseNpgsql(connectionString));

builder.Services.AddSingleton<StoragePaths>();
builder.Services.AddSingleton<DemoClock>();
builder.Services.AddScoped<DemoSeeder>();
builder.Services.AddSingleton<SigningKeyProvider>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddMemoryCache(o => o.SizeLimit = 64 * 1024 * 1024); // obrazki PNG: maks. 64 MB w pamięci
builder.Services.AddSingleton<PngImages>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearer>();
builder.Services.AddAuthorization();

// Koder jawnie: formatter MVC bez kodera bierze UnsafeRelaxedJsonEscaping, a JsonSerializer – domyślny (ł → \u0142);
// z jednym koderem zapisana odpowiedź (Idempotency-Key) jest bajt w bajt taka sama jak pierwsza
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    });
builder.Services.AddProblemDetails();
builder.Services.AddFieldServiceSwagger();
builder.Services.AddFieldServiceRateLimiting();
builder.AddFieldServiceTelemetry(HealthController.Version);

// API stoi za reverse proxy: adres klienta i https z nagłówków X-Forwarded-*
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// tabele tworzone przy pierwszym starcie (bez migracji), wspólny katalog części i zapasy
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<FieldServiceDbContext>().Database.EnsureCreated();
    scope.ServiceProvider.GetRequiredService<DemoSeeder>().EnsureCatalog();
}

// ---------- potok ----------
app.UseForwardedHeaders();

var pathBase = NormalizePathBase(app.Configuration["PathBase"]);
if (pathBase.Length > 0)
{
    app.UsePathBase(pathBase); // /maui/architektura/api/... → PathBase=/maui/architektura, Path=/api/...
}

// żądania spoza PathBase → 301 na adres z konfiguracji; pusty adres = wyłączone (za reverse proxy robi to proxy)
var outsideRedirect = app.Configuration["RedirectOutsidePathBase"];
if (pathBase.Length > 0 && !string.IsNullOrWhiteSpace(outsideRedirect))
{
    app.Use((context, next) =>
    {
        if (context.Request.PathBase.HasValue) return next(context);
        context.Response.Redirect(outsideRedirect, permanent: true);
        return Task.CompletedTask;
    });
}

app.UseExceptionHandler();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Configuration.GetValue("Swagger:Enabled", true))
{
    app.UseFieldServiceSwagger();
}

app.MapControllers();
app.MapGet("/", (HttpContext ctx) => Results.Content(HomePage.Render(ctx.Request.PathBase), "text/html; charset=utf-8"))
    .ExcludeFromDescription();

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("FieldService API {Version} | PathBase: '{PathBase}' | telemetria OTLP: {Telemetry}",
        HealthController.Version, pathBase, Telemetry.ExportEnabled(app.Configuration) ? "tak" : "nie");
    if (!explicitUrls && hosting.ListenOnAllInterfaces)
    {
        foreach (var ip in LanAddresses())
            app.Logger.LogInformation("Sieć sali: http://{Ip}:{Port}{PathBase}/swagger", ip, hosting.Port, pathBase);
    }
});

app.Run();

static string NormalizePathBase(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return "";
    var trimmed = value.Trim().TrimEnd('/');
    return trimmed.Length == 0 ? "" : trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
}

static IEnumerable<IPAddress> LanAddresses() =>
    NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

/// <summary>Punkt wejścia widoczny dla testów (WebApplicationFactory).</summary>
public partial class Program;
