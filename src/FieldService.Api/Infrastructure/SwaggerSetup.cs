using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using FieldService.Api.Controllers;
using FieldService.Api.Domain;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace FieldService.Api.Infrastructure;

public static class SwaggerSetup
{
    public const string BearerScheme = "Bearer";

    public static IServiceCollection AddFieldServiceSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "FieldService API (szkoleniowe)",
                Version = "v1",
                Description =
                    "API szkoleniowe do aplikacji FieldService (.NET MAUI) — szkolenie „Architektura aplikacji w .NET MAUI”.\n\n" +
                    "**Jak zacząć:** `POST /api/auth/login` z dowolnym poprawnym e-mailem i PIN-em `1234` → skopiuj " +
                    "`accessToken` → przycisk **Authorize** → wklej token. Przy pierwszym logowaniu w danym dniu serwer " +
                    "tworzy dzień pracy: 4 wizyty na dziś (jedna rozpoczęta), zlecenia, auto technika i historię wizyt.\n\n" +
                    "**Komendy wizyty** wymagają `If-Match` (albo `X-If-Match` – proxy po drodze może wyciąć `If-Match`) z wersją wizyty " +
                    "(`ETag` z `GET api/visits/{id}`; inna wersja → 409 " +
                    "`Visit.VersionConflict`, brak → 428) i przyjmują `Idempotency-Key` (powtórka z tym samym kluczem zwraca " +
                    "zapisaną odpowiedź). Listy i katalog mają `ETag` — `If-None-Match` z tym samym ETagiem → 304.\n\n" +
                    "Błędy to ProblemDetails z polem `code` (np. `Visit.SignatureRequired`, `Inventory.OutOfStock`). " +
                    "Wyliczenia w JSON są tekstowe, daty w ISO 8601. Endpointy **Dispatcher** symulują dyspozytora."
            });

            var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
            if (File.Exists(xml)) c.IncludeXmlComments(xml, includeControllerXmlComments: true);

            c.AddSecurityDefinition(BearerScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Wklej `accessToken` z `POST /api/auth/login` (bez słowa Bearer)."
            });
            c.OperationFilter<AuthorizeOperationFilter>();
            c.OperationFilter<HeaderParametersOperationFilter>();
            c.SchemaFilter<EnumDescriptionSchemaFilter>();
            c.DocumentFilter<TagDescriptionsDocumentFilter>();
            // pole-referencja (enum, obiekt) zachowuje „nullable” – generator klienta (NSwag) da np. VisitOutcomeType?
            c.UseAllOfToExtendReferenceSchemas();
            c.SupportNonNullableReferenceTypes();
            c.NonNullableReferenceTypesAsRequired();
        });
        return services;
    }

    public static WebApplication UseFieldServiceSwagger(this WebApplication app)
    {
        app.UseSwagger(o => o.PreSerializeFilters.Add((doc, request) =>
        {
            // Za reverse proxy z PathBase (/maui/architektura) „Try it out” musi trafiać pod prefiks.
            doc.Servers = [new OpenApiServer { Url = request.PathBase.HasValue ? request.PathBase.Value : "/" }];
        }));
        app.UseSwaggerUI(o =>
        {
            o.SwaggerEndpoint("v1/swagger.json", "FieldService API v1"); // względnie → działa z każdym PathBase
            o.DocumentTitle = "FieldService API — Swagger";
            o.EnableTryItOutByDefault();
            o.DisplayRequestDuration();
            o.EnablePersistAuthorization();
        });
        return app;
    }

    private sealed class AuthorizeOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
            if (!metadata.OfType<IAuthorizeData>().Any() || metadata.OfType<IAllowAnonymous>().Any()) return;

            operation.Security =
            [
                new OpenApiSecurityRequirement
                {
                    [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = BearerScheme } }] = []
                }
            ];
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Brak lub wygasły access token (odśwież przez /api/auth/refresh)." });
        }
    }

    // Nagłówki widoczne w „Try it out”: opisy If-Match / X-If-Match / Idempotency-Key, pole X-Dispatcher-Key dla dyspozytora
    private sealed class HeaderParametersOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            foreach (var parameter in operation.Parameters ?? [])
            {
                parameter.Description = parameter.Name switch
                {
                    "If-Match" => "Wersja wizyty, którą widzi telefon, np. `\"3\"` (ETag z `GET api/visits/{id}`). Inna wersja na serwerze → 409 `Visit.VersionConflict`; brak → 428.",
                    "X-If-Match" => "To samo co `If-Match` – proxy po drodze może wyciąć `If-Match`, więc wpisz wersję tutaj.",
                    "Idempotency-Key" => "Dowolny unikalny klucz (np. GUID). Powtórka z tym samym kluczem zwraca zapisaną odpowiedź – zmiana nie wykona się drugi raz.",
                    "X-Admin-Key" => "Klucz administratora (tylko trener).",
                    _ => parameter.Description
                };
            }

            var dispatcher = context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<DispatcherKeyAttribute>().Any();
            if (!dispatcher) return;
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "X-Dispatcher-Key",
                In = ParameterLocation.Header,
                Required = false,
                Description = "Klucz dyspozytora – wymagany, gdy serwer ma ustawiony `Dispatcher:Key` (na szkoleniu: tylko trener).",
                Schema = new OpenApiSchema { Type = "string" }
            });
        }
    }

    private sealed class EnumDescriptionSchemaFilter : ISchemaFilter
    {
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            if (context.Type == typeof(VisitStatus))
            {
                schema.Description = "Status wizyty: `Planned` = Zaplanowana, `InProgress` = W trakcie, `Completed` = Zakończona, " +
                                     "`Cancelled` = Anulowana. Przejścia pilnuje serwer (np. zakończonej nie można anulować).";
                schema.Example = new OpenApiString("Planned");
            }
            else if (context.Type == typeof(VisitOutcomeType))
            {
                schema.Description = "Wynik wizyty: `Fixed` = Naprawione (wymaga podpisu klienta, zamyka zlecenie), " +
                                     "`NeedsFollowUp` = Potrzebna kolejna wizyta, `NotFixed` = Nie naprawiono (oba wymagają powodu).";
                schema.Example = new OpenApiString("Fixed");
            }
            else if (context.Type == typeof(WorkOrderStatus))
            {
                schema.Description = "Status zlecenia: `Open` = Otwarte, `Closed` = Zamknięte (po wizycie z wynikiem `Fixed`).";
                schema.Example = new OpenApiString("Open");
            }
        }
    }

    private sealed class TagDescriptionsDocumentFilter : IDocumentFilter
    {
        public void Apply(OpenApiDocument doc, DocumentFilterContext context)
        {
            doc.Tags =
            [
                new OpenApiTag { Name = "Auth", Description = "Logowanie technika (PIN `1234`) i odświeżanie tokenu (refresh token jednorazowy)." },
                new OpenApiTag { Name = "Visits", Description = "Wizyty technika: lista na dziś, historia, szczegóły i komendy (If-Match / X-If-Match, Idempotency-Key)." },
                new OpenApiTag { Name = "Parts", Description = "Magazyn części: katalog, magazyny, stan, auto technika i rezerwacje (ostatnia sztuka → 409 `Inventory.OutOfStock`)." },
                new OpenApiTag { Name = "WorkOrders", Description = "Zlecenia technika (model odczytu „Moje zlecenia”)." },
                new OpenApiTag { Name = "Dispatcher", Description = "Symulacja dyspozytora (trener): anulowanie i przeniesienie wizyty, nowa wizyta, stan magazynu. Nagłówek X-Dispatcher-Key." },
                new OpenApiTag { Name = "Images", Description = "Zdjęcia części i urządzeń (bez logowania; `size=thumb` – miniatura)." },
                new OpenApiTag { Name = "Admin", Description = "Administracja: reset danych (nagłówek X-Admin-Key)." },
                new OpenApiTag { Name = "Health", Description = "Stan usługi." }
            ];
        }
    }
}
