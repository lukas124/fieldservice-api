using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace FieldService.Api.Infrastructure;

/// <summary>
/// OpenTelemetry API: ślady (ASP.NET Core, HttpClient, Npgsql), metryki i logi. Ślad z telefonu (<c>traceparent</c>)
/// ciągnie się przez kontroler aż do zapytania SQL. Eksport OTLP tylko, gdy ustawiono
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (pozostałe <c>OTEL_EXPORTER_OTLP_*</c> czyta eksporter).
/// </summary>
public static class Telemetry
{
    public const string ServiceName = "fieldservice-api";

    public static bool ExportEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

    public static WebApplicationBuilder AddFieldServiceTelemetry(this WebApplicationBuilder builder, string version)
    {
        if (!ExportEnabled(builder.Configuration)) return builder;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: version)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/api/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithLogging()
            .UseOtlpExporter();
        return builder;
    }
}
