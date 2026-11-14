using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Notes.Api.Authentication;
using Notes.Api.Data;
using Notes.Api.Endpoints;
using Notes.Api.Http;
using Notes.Api.Validation;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<NotesDbContext>((services, options) =>
{
    var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString("Notes");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("ConnectionStrings:Notes is required.");
    }

    NpgsqlConnectionStringBuilder connection;
    try
    {
        connection = new NpgsqlConnectionStringBuilder(connectionString);
    }
    catch (ArgumentException)
    {
        throw new InvalidOperationException("ConnectionStrings:Notes must be a valid PostgreSQL connection string.");
    }

    if (string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Database) ||
        string.IsNullOrWhiteSpace(connection.Username))
    {
        throw new InvalidOperationException("ConnectionStrings:Notes must specify Host, Database, and Username.");
    }

    connection.IncludeErrorDetail = false;
    options.UseNpgsql(connection.ConnectionString).EnableSensitiveDataLogging(false);
});
builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter<Notes.Api.Contracts.GrantAudience>(JsonNamingPolicy.CamelCase, false));
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter<Notes.Api.Contracts.GrantPermission>(JsonNamingPolicy.CamelCase, false));
});
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = InputValidation.MaxRequestBytes);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await DatabaseInitializer.InitializeAsync(
        scope.ServiceProvider.GetRequiredService<NotesDbContext>(),
        app.Configuration,
        app.Lifetime.ApplicationStopping);
}

app.UseMiddleware<ApiErrorMiddleware>();
app.UseStatusCodePages(async context =>
{
    await ApiProblems.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode,
        "The request could not be completed.");
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", async (NotesDbContext database, CancellationToken cancellationToken) =>
    await database.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ok" })
        : Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous();
app.MapNotesApi();

await app.RunAsync();

public partial class Program;
