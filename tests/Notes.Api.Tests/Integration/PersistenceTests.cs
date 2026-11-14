using System.Net;
using System.Net.Http.Headers;
using Notes.Api.Authentication;
using Notes.Api.Tests.Infrastructure;
using Npgsql;

namespace Notes.Api.Tests.Integration;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PersistenceTests(PostgreSqlFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Organization_grants_cover_authenticated_users_with_no_team_memberships()
    {
        var userId = Guid.NewGuid();
        var key = PostgreSqlFixture.NewApiKey();
        await Fixture.ExecuteAsync(
            "INSERT INTO users(id, display_name, api_key_hash) VALUES (@id, @name, @hash)",
            new NpgsqlParameter("id", userId),
            new NpgsqlParameter("name", $"Unassigned {userId:N}"),
            new NpgsqlParameter("hash", ApiKeys.Hash(key)));
        try
        {
            Assert.Equal(0, await CountAsync("team_memberships", "user_id", userId));
            using var unassigned = new HttpClient { BaseAddress = Fixture.Anonymous.BaseAddress };
            unassigned.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var note = await CreateNoteAsync();
            using (var shared = await PutSharingAsync(Alice, note.Note.Id, note.ETag,
                new TestGrant("organization", null, "read")))
            {
                Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
            }

            await GetNoteAsync(unassigned, note.Note.Id);
            Assert.Contains(await AllNotesAsync(unassigned), item => item.Id == note.Note.Id);
            using (var readOnly = await PutContentAsync(unassigned, note.Note.Id, Tag(2)))
            {
                await AssertProblemAsync(readOnly, HttpStatusCode.Forbidden, noVersion: true);
            }

            using (var anonymous = await Fixture.Anonymous.GetAsync($"/api/notes/{note.Note.Id}"))
            {
                await AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized, authenticated: false, noVersion: true);
            }

            using (var editGrant = await PutSharingAsync(Alice, note.Note.Id, Tag(2),
                new TestGrant("organization", null, "edit")))
            {
                Assert.Equal(HttpStatusCode.OK, editGrant.StatusCode);
            }

            using var edit = await PutContentAsync(unassigned, note.Note.Id, Tag(3));
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            Assert.Equal(note.Note.OwnerId, (await ReadNoteAsync(edit)).Note.OwnerId);
        }
        finally
        {
            await Fixture.ExecuteAsync("DELETE FROM users WHERE id = @id", new NpgsqlParameter("id", userId));
        }
    }

    [Fact]
    public async Task Migrations_and_seed_are_idempotent_and_note_state_survives_application_restart()
    {
        var teams = await TeamsAsync();
        var note = await CreateNoteAsync(title: "Persist across a new application host");
        using (var shared = await PutSharingAsync(Alice, note.Note.Id, note.ETag,
            new("team", teams.TeamA, "edit"), new("organization", null, "read")))
        {
            Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        }

        using (var edited = await PutContentAsync(Bob, note.Note.Id, Tag(2),
            "Edited before restart", "Durable PostgreSQL content"))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        }

        var before = await GetNoteAsync(Alice, note.Note.Id);
        var sharingBefore = await Alice.GetStringAsync($"/api/notes/{note.Note.Id}/sharing");
        var identityBefore = await IdentityRowsAsync();
        var noteCount = await CountAsync("notes");
        var grantCount = await CountAsync("note_grants");
        Assert.True(await CountAsync("\"__EFMigrationsHistory\"") > 0);

        await Fixture.RestartAsync();
        Assert.Equal(before, await GetNoteAsync(Alice, note.Note.Id));
        Assert.Equal(sharingBefore, await Alice.GetStringAsync($"/api/notes/{note.Note.Id}/sharing"));
        Assert.Equal(identityBefore, await IdentityRowsAsync());
        Assert.Equal(noteCount, await CountAsync("notes"));
        Assert.Equal(grantCount, await CountAsync("note_grants"));
        Assert.Equal(1, await CountAsync("seed_state"));
        await GetNoteAsync(Bob, note.Note.Id);
        await GetNoteAsync(Carol, note.Note.Id);
    }

    [Fact]
    public async Task Restart_does_not_resurrect_deleted_notes_or_example_grants()
    {
        await Fixture.ExecuteAsync("DELETE FROM notes");
        Assert.Equal(0, await CountAsync("notes"));
        Assert.Equal(0, await CountAsync("note_grants"));
        await Fixture.RestartAsync();
        Assert.Equal(0, await CountAsync("notes"));
        Assert.Equal(0, await CountAsync("note_grants"));
        Assert.Equal(1, await CountAsync("seed_state"));
        Assert.Empty((await ListAsync(Alice)).Items);
    }

    [Fact]
    public async Task Configured_key_rotation_reconciles_hashes_and_invalidates_the_previous_key()
    {
        var oldKey = Fixture.ApiKey(TestUser.Alice);
        var replacement = PostgreSqlFixture.NewApiKey();
        var note = await CreateNoteAsync();
        var identityBefore = await IdentityRowsAsync();
        var oldHash = await UserHashAsync(note.Note.OwnerId);
        Assert.Equal(ApiKeys.Hash(oldKey), oldHash);
        Assert.NotEqual(oldKey, oldHash);
        try
        {
            await Fixture.RestartAsync(new Dictionary<TestUser, string> { [TestUser.Alice] = replacement });
            Assert.Equal(note, await GetNoteAsync(Alice, note.Note.Id));
            Assert.Equal(identityBefore, await IdentityRowsAsync());
            var newHash = await UserHashAsync(note.Note.OwnerId);
            Assert.Equal(ApiKeys.Hash(replacement), newHash);
            Assert.NotEqual(oldHash, newHash);
            Assert.NotEqual(replacement, newHash);

            foreach (var invalidToken in new[] { oldKey, newHash })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/notes");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", invalidToken);
                using var response = await Fixture.Anonymous.SendAsync(request);
                await AssertProblemAsync(response, HttpStatusCode.Unauthorized, authenticated: false, noVersion: true);
            }

            foreach (var user in new[] { TestUser.Bob, TestUser.Carol, TestUser.Dana })
            {
                using var response = await Fixture.Client(user).GetAsync("/api/teams");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
        finally
        {
            await Fixture.RestartAsync(new Dictionary<TestUser, string> { [TestUser.Alice] = oldKey });
        }
    }

    [Fact]
    public async Task Persisted_seed_credentials_are_hashes_not_plaintext_keys()
    {
        await using var connection = await Fixture.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT display_name, api_key_hash FROM users WHERE display_name IN ('Alice','Bob','Carol','Dana')",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            var user = Enum.Parse<TestUser>(reader.GetString(0));
            var stored = reader.GetString(1);
            Assert.Matches("^[0-9A-F]{64}$", stored);
            Assert.Equal(ApiKeys.Hash(Fixture.ApiKey(user)), stored);
            Assert.NotEqual(Fixture.ApiKey(user), stored);
            count++;
        }

        Assert.Equal(4, count);
    }

    private async Task<long> CountAsync(string table, string? column = null, Guid? value = null)
    {
        await using var connection = await Fixture.OpenConnectionAsync();
        // Identifiers here are fixed test constants, never supplied by configuration or HTTP.
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {table}{(column is null ? "" : $" WHERE {column} = @value")}", connection);
        if (value is not null)
        {
            command.Parameters.AddWithValue("value", value.Value);
        }

        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }

    private async Task<string> UserHashAsync(Guid userId)
    {
        await using var connection = await Fixture.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT api_key_hash FROM users WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", userId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private async Task<IReadOnlyList<string>> IdentityRowsAsync()
    {
        await using var connection = await Fixture.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT 'user:' || id || ':' || display_name AS identity FROM users
            UNION ALL
            SELECT 'team:' || id || ':' || name FROM teams
            UNION ALL
            SELECT 'membership:' || user_id || ':' || team_id FROM team_memberships
            ORDER BY identity
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        List<string> rows = [];
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
