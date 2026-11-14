using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Notes.Api.Validation;

namespace Notes.Api.Http;

public sealed class ApiErrorMiddleware(RequestDelegate next, ILogger<ApiErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var isApi = context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
        if (isApi)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
        }

        var originalBody = context.Request.Body;
        try
        {
            if (isApi)
            {
                if (context.Request.ContentLength > InputValidation.MaxRequestBytes)
                {
                    await ApiProblems.WriteAsync(context, StatusCodes.Status413PayloadTooLarge,
                        "Request body must not exceed 1 MiB.");
                    return;
                }

                var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (sizeFeature is { IsReadOnly: false })
                {
                    sizeFeature.MaxRequestBodySize = InputValidation.MaxRequestBytes;
                }

                context.Request.Body = new BoundedReadStream(originalBody, InputValidation.MaxRequestBytes);
            }

            await next(context);
        }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            var detail = exception.StatusCode switch
            {
                StatusCodes.Status413PayloadTooLarge => "Request body must not exceed 1 MiB.",
                StatusCodes.Status415UnsupportedMediaType => "Content-Type must be application/json.",
                _ => "The request is malformed or contains invalid fields."
            };
            await ApiProblems.WriteAsync(context, exception.StatusCode, detail);
        }
        catch (JsonException) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            await ApiProblems.WriteAsync(context, StatusCodes.Status400BadRequest,
                "The request must be valid JSON with only the documented fields and value types.");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Request {TraceId} was canceled by the client.", context.TraceIdentifier);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            }
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            logger.LogError("Unhandled {ExceptionType} in request {TraceId}. Stack: {StackTrace}",
                exception.GetType().FullName, context.TraceIdentifier, exception.StackTrace);
            context.Response.Clear();
            await ApiProblems.WriteAsync(context, StatusCodes.Status500InternalServerError,
                "An unexpected server error occurred. Use the trace ID when reporting this error.");
        }
        finally
        {
            context.Request.Body = originalBody;
        }
    }
}
