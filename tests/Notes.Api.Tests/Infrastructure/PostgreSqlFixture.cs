using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Notes.Api.Tests.Infrastructure;

public enum TestUser
{
    Alice,
    Bob,
    Carol,
    Dana
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "Real PostgreSQL";
}

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly string databaseName = $"notes_tests_{Guid.NewGuid():N}";
    private readonly Dictionary<TestUser, HttpClient> clients = [];
    private readonly Dictionary<TestUser, string> keys =
        Enum.GetValues<TestUser>().ToDictionary(user => user, _ => NewApiKey());
    private string administrationConnectionString = "";
    private bool databaseCreated;
    private NotesApplicationFactory? factory;
    private HttpClient? anonymousClient;

    public string ConnectionString { get; private set; } = "";
    public string ApplicationName => databaseName;

    public HttpClient Anonymous => anonymousClient ??
        throw new InvalidOperationException("The PostgreSQL fixture has not started.");

    public static string NewApiKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public string ApiKey(TestUser user) => keys[user];

    public HttpClient Client(TestUser user) => clients[user];

    public async Task InitializeAsync()
    {
        var supplied = Environment.GetEnvironmentVariable("NOTES_TEST_DB");
        if (string.IsNullOrWhiteSpace(supplied))
        {
            throw new InvalidOperationException(
                "Integration tests require real PostgreSQL. Set NOTES_TEST_DB to an administration " +
                "connection string for a reachable PostgreSQL server, with permission to CREATE DATABASE. " +
                "The suite creates and drops only its own notes_tests_<random> database; it never " +
                "resets the supplied database. Run --filter Category=Unit for database-free tests.");
        }

        var administration = new NpgsqlConnectionStringBuilder(supplied)
        {
            Pooling = false,
            ApplicationName = $"{databaseName}_admin",
            Timeout = 10
        };
        administrationConnectionString = administration.ConnectionString;
        var application = new NpgsqlConnectionStringBuilder(supplied)
        {
            Database = databaseName,
            ApplicationName = ApplicationName,
            Timeout = 10
        };
        ConnectionString = application.ConnectionString;

        await using var connection = new NpgsqlConnection(administrationConnectionString);
        try
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
            databaseCreated = true;
        }
        catch (NpgsqlException exception)
        {
            throw new InvalidOperationException(
                "Could not create the isolated PostgreSQL test database. Check NOTES_TEST_DB, " +
                "server reachability, and CREATEDB privileges. No supplied database was reset.", exception);
        }

        try
        {
            await StartApplicationAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task RestartAsync(IReadOnlyDictionary<TestUser, string>? replacementKeys = null)
    {
        await StopApplicationAsync();
        if (replacementKeys is not null)
        {
            foreach (var (user, key) in replacementKeys)
            {
                keys[user] = key;
            }
        }

        await StartApplicationAsync();
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<int> ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await StopApplicationAsync();
        if (!databaseCreated)
        {
            return;
        }

        // This name is generated here, never obtained from the supplied connection string.
        await using var connection = new NpgsqlConnection(administrationConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE \"{databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
        databaseCreated = false;
    }

    private async Task StartApplicationAsync()
    {
        factory = new NotesApplicationFactory(ConnectionString, keys);
        factory.UseKestrel(0);
        anonymousClient = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        foreach (var user in Enum.GetValues<TestUser>())
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", keys[user]);
            clients.Add(user, client);
        }

        using var health = await anonymousClient.GetAsync("/health");
        Assert.True(health.IsSuccessStatusCode,
            $"The real application failed its startup health check: {(int)health.StatusCode}.");
    }

    private async Task StopApplicationAsync()
    {
        foreach (var client in clients.Values)
        {
            client.Dispose();
        }

        clients.Clear();
        anonymousClient?.Dispose();
        anonymousClient = null;
        if (factory is not null)
        {
            await factory.DisposeAsync();
            factory = null;
        }
    }
}

internal sealed class NotesApplicationFactory(
    string connectionString,
    IReadOnlyDictionary<TestUser, string> keys) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Notes"] = connectionString
        };
        foreach (var (user, key) in keys)
        {
            configuration[$"Demo:ApiKeys:{user}"] = key;
        }

        builder.UseEnvironment("Testing");
        foreach (var (name, value) in configuration)
        {
            builder.UseSetting(name, value);
        }

        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            configurationBuilder.AddInMemoryCollection(configuration));
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
    }
}
