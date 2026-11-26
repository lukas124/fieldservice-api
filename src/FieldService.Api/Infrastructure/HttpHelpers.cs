using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FieldService.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace FieldService.Api.Infrastructure;

public static class Problems
{
    /// <summary>ProblemDetails z kodem błędu w polu <c>code</c> (aplikacja mapuje kod na <c>Error</c>, nie tekst).</summary>
    public static ObjectResult Refuse(this ControllerBase controller, ApiError error, IDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Status = error.Status,
            Title = error.Title,
            Detail = error.Detail,
            Instance = controller.Request.Path,
            Extensions = { ["code"] = error.Code }
        };
        foreach (var (key, value) in extensions ?? new Dictionary<string, object?>()) problem.Extensions[key] = value;
        return new ObjectResult(problem) { StatusCode = error.Status, ContentTypes = { "application/problem+json" } };
    }
}

public static class ETags
{
    /// <summary>
    /// 200 z nagłówkiem <c>ETag</c> (skrót treści) albo 304, gdy klient przysłał ten sam ETag w <c>If-None-Match</c>.
    /// <c>Cache-Control: no-cache</c> — klient może trzymać kopię, ale przed użyciem pyta serwer.
    /// </summary>
    public static IActionResult OkWithETag<T>(this ControllerBase controller, T body)
    {
        var json = controller.HttpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body, json));
        var etag = $"\"{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}\"";

        controller.Response.Headers.ETag = etag;
        controller.Response.Headers.CacheControl = "private, no-cache";
        var ifNoneMatch = controller.Request.Headers.IfNoneMatch.ToString();
        if (ifNoneMatch.Split(',').Any(t => t.Trim() == etag || t.Trim() == "W/" + etag))
            return new StatusCodeResult(StatusCodes.Status304NotModified);
        return controller.Ok(body);
    }

    /// <summary><c>"3"</c>, <c>W/"3"</c> albo <c>3</c> → 3; brak lub śmieci → null.</summary>
    public static int? ParseVersion(string? ifMatch)
    {
        if (string.IsNullOrWhiteSpace(ifMatch)) return null;
        var value = ifMatch.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal)) value = value[2..];
        return int.TryParse(value.Trim('"'), out var version) ? version : null;
    }

    public static string ForVersion(int version) => $"\"{version}\"";
}

public static class Secrets
{
    public static bool FixedTimeEquals(string? provided, string expected) =>
        provided is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Sztuczne opóźnienie listy (<c>Demo:ListDelayMilliseconds</c>) — tylko do pomiarów cache w Bloku 14.</summary>
public sealed class DemoDelayAttribute : ActionFilterAttribute
{
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var delay = context.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<DemoOptions>>().CurrentValue.ListDelayMilliseconds;
        if (delay > 0) await Task.Delay(delay, context.HttpContext.RequestAborted);
        await next();
    }
}

public static class Urls
{
    /// <summary>Pełny adres obrazka (za proxy: https + host i PathBase z nagłówków X-Forwarded-*).</summary>
    public static string Image(HttpRequest request, string key, bool thumbnail) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}/api/images/{key}{(thumbnail ? "?size=thumb" : "")}";
}
