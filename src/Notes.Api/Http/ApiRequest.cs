using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Notes.Api.Http;

internal static class ApiRequest
{
    public static async Task<T> ReadJsonAsync<T>(HttpContext context) where T : class
    {
        if (!context.Request.HasJsonContentType())
        {
            throw new BadHttpRequestException("Content-Type must be application/json.",
                StatusCodes.Status415UnsupportedMediaType);
        }

        var options = context.RequestServices
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>();
        return await JsonSerializer.DeserializeAsync<T>(
                   context.Request.Body, options.Value.SerializerOptions, context.RequestAborted)
               ?? throw new BadHttpRequestException("A JSON request object is required.");
    }
}
