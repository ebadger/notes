using Notes.Api.Data;
using Notes.Api.Http;

namespace Notes.Api.Endpoints;

public static class DemoEndpoints
{
    public static void MapDemoApi(this IEndpointRouteBuilder endpoints)
    {
        var configuration = endpoints.ServiceProvider.GetRequiredService<IConfiguration>();
        var keys = configuration.GetValue("Demo:EnableBrowserKeys", false)
            ? DatabaseInitializer.ReadConfiguredKeys(configuration)
            : null;
        if (keys is not null)
        {
            endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DemoEndpoints))
                .LogWarning("Local demo browser access is enabled. Anyone who can open the local page can use every demo identity.");
        }

        endpoints.MapGet("/demo/keys", IResult (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (keys is null || !IsLocalBrowserRequest(context.Request))
            {
                return ApiProblems.Result(context, StatusCodes.Status404NotFound,
                    "The requested resource was not found.");
            }

            return TypedResults.Ok(new { keys });
        }).AllowAnonymous();
    }

    private static bool IsLocalBrowserRequest(HttpRequest request)
    {
        if (!HasSingleHeader(request, "X-Notes-Demo", "true") ||
            !HasSingleHeader(request, "Sec-Fetch-Site", "same-origin"))
        {
            return false;
        }

        var host = request.Host.Host;
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
            host is not ("127.0.0.1" or "[::1]" or "::1"))
        {
            return false;
        }

        if (request.Scheme is not ("http" or "https") ||
            !Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var requestOrigin))
        {
            return false;
        }

        var origin = request.Headers.Origin;
        return !request.Headers.ContainsKey("Origin") ||
            (origin.Count == 1 && string.Equals(origin[0],
                requestOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasSingleHeader(HttpRequest request, string name, string value)
    {
        var header = request.Headers[name];
        return header.Count == 1 && string.Equals(header[0], value, StringComparison.Ordinal);
    }
}
