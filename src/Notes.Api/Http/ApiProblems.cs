using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Notes.Api.Http;

public static class ApiProblems
{
    public static IResult Result(HttpContext context, int status, string detail) =>
        TypedResults.Problem(new ProblemDetails
        {
            Type = "about:blank",
            Status = status,
            Title = ReasonPhrases.GetReasonPhrase(status),
            Detail = detail,
            Instance = context.Request.Path,
            Extensions = { ["traceId"] = context.TraceIdentifier }
        });

    public static IResult Validation(HttpContext context, IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            detail: "Correct the indicated request fields.",
            instance: context.Request.Path,
            extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier });

    public static Task WriteAsync(HttpContext context, int status, string detail) =>
        Result(context, status, detail).ExecuteAsync(context);
}
