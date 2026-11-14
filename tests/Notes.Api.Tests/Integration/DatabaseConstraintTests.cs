using System.Net;
using Notes.Api.Tests.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Notes.Api.Tests.Integration;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DatabaseConstraintTests(PostgreSqlFixture fixture) : ApiTestBase(fixture)
{
    [Theory]
    [InlineData("public", false, "read")]
    [InlineData("organization", false, "owner")]
    [InlineData("organization", false, "READ")]
    [InlineData("team", false, "read")]
    [InlineData("organization", true, "read")]
    public async Task PostgreSQL_itself_rejects_invalid_audience_permission_and_team_combinations(
        string audience,
        bool includeTeam,
        string permission)
    {
        var teams = await TeamsAsync();
        var note = await CreateNoteAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertGrantAsync(note.Note.Id, audience, includeTeam ? teams.TeamA : null, permission));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.False(string.IsNullOrWhiteSpace(exception.ConstraintName));
        Assert.Equal(0, await GrantCountAsync(note.Note.Id));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task PostgreSQL_itself_requires_positive_versions(long version)
    {
        var note = await CreateNoteAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => Fixture.ExecuteAsync(
            "UPDATE notes SET version = @version WHERE id = @id",
            new NpgsqlParameter("version", version),
            new NpgsqlParameter("id", note.Note.Id)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(note, await GetNoteAsync(Alice, note.Note.Id));
    }

    [Fact]
    public async Task PostgreSQL_itself_rejects_empty_note_titles()
    {
        var note = await CreateNoteAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => Fixture.ExecuteAsync(
            "UPDATE notes SET title = '' WHERE id = @id",
            new NpgsqlParameter("id", note.Note.Id)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(note, await GetNoteAsync(Alice, note.Note.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSQL_itself_enforces_the_body_byte_limit(bool multibyte)
    {
        var note = await CreateNoteAsync();
        var exact = multibyte ? new string('\u00e9', 32768) : new string('x', 65536);
        Assert.Equal(65536, System.Text.Encoding.UTF8.GetByteCount(exact));
        await Fixture.ExecuteAsync("UPDATE notes SET body = @body WHERE id = @id",
            new NpgsqlParameter("body", exact),
            new NpgsqlParameter("id", note.Note.Id));
        var exception = await Assert.ThrowsAsync<PostgresException>(() => Fixture.ExecuteAsync(
            "UPDATE notes SET body = @body WHERE id = @id",
            new NpgsqlParameter("body", exact + "x"),
            new NpgsqlParameter("id", note.Note.Id)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(exact, (await GetNoteAsync(Alice, note.Note.Id)).Note.Body);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("lowercase")]
    [InlineData("nonhex")]
    public async Task PostgreSQL_itself_requires_a_sha256_hex_key_hash(string scenario)
    {
        var hash = scenario switch
        {
            "short" => "short",
            "lowercase" => new string('a', 64),
            "nonhex" => new string('G', 64),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var exception = await Assert.ThrowsAsync<PostgresException>(() => Fixture.ExecuteAsync(
            "INSERT INTO users(id, display_name, api_key_hash) VALUES (@id, 'Invalid hash', @hash)",
            new NpgsqlParameter("id", Guid.NewGuid()),
            new NpgsqlParameter("hash", hash)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Seed_marker_constraint_permits_only_the_singleton_identity()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => Fixture.ExecuteAsync(
            "INSERT INTO seed_state(id, initialized_at) VALUES (2, CURRENT_TIMESTAMP)"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Team_and_organization_grants_have_database_enforced_per_note_uniqueness()
    {
        var teams = await TeamsAsync();
        var first = await CreateNoteAsync();
        var second = await CreateNoteAsync();
        foreach (var note in new[] { first, second })
        {
            await InsertGrantAsync(note.Note.Id, "team", teams.TeamA, "read");
            await InsertGrantAsync(note.Note.Id, "organization", null, "read");
        }

        var teamDuplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertGrantAsync(first.Note.Id, "team", teams.TeamA, "edit"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, teamDuplicate.SqlState);
        var organizationDuplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertGrantAsync(first.Note.Id, "organization", null, "edit"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, organizationDuplicate.SqlState);
        Assert.Equal(2, await GrantCountAsync(first.Note.Id));
        Assert.Equal(2, await GrantCountAsync(second.Note.Id));

        await using var connection = await Fixture.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM pg_index index
            JOIN pg_class relation ON relation.oid = index.indrelid
            JOIN pg_namespace schema ON schema.oid = relation.relnamespace
            WHERE schema.nspname = 'public' AND relation.relname = 'note_grants'
              AND index.indisunique AND index.indpred IS NOT NULL
            """, connection);
        Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync()) >= 2,
            "Expected partial unique indexes for team targets and the null-team organization target.");
    }

    [Fact]
    public async Task Database_foreign_keys_reject_unknown_notes_teams_and_owners()
    {
        var teams = await TeamsAsync();
        var note = await CreateNoteAsync();
        var missingNote = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertGrantAsync(Guid.NewGuid(), "team", teams.TeamA, "read"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missingNote.SqlState);
        var missingTeam = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertGrantAsync(note.Note.Id, "team", Guid.NewGuid(), "read"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missingTeam.SqlState);
        var missingOwner = await Assert.ThrowsAsync<PostgresException>(() => Fixture.ExecuteAsync(
            "UPDATE notes SET owner_id = @owner WHERE id = @id",
            new NpgsqlParameter("owner", Guid.NewGuid()),
            new NpgsqlParameter("id", note.Note.Id)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missingOwner.SqlState);
        Assert.Equal(note, await GetNoteAsync(Alice, note.Note.Id));
    }

    [Fact]
    public async Task Membership_composite_key_and_api_key_hash_uniqueness_are_enforced()
    {
        var teams = await TeamsAsync();
        var note = await CreateNoteAsync();
        var membership = await Assert.ThrowsAsync<PostgresException>(() => Fixture.ExecuteAsync(
            "INSERT INTO team_memberships(user_id, team_id) VALUES (@user, @team)",
            new NpgsqlParameter("user", note.Note.OwnerId),
            new NpgsqlParameter("team", teams.TeamA)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, membership.SqlState);
        var duplicateKey = await Assert.ThrowsAsync<PostgresException>(() => Fixture.ExecuteAsync(
            """
            INSERT INTO users(id, display_name, api_key_hash)
            SELECT @id, 'Duplicate key test', api_key_hash FROM users WHERE id = @source
            """,
            new NpgsqlParameter("id", Guid.NewGuid()),
            new NpgsqlParameter("source", note.Note.OwnerId)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateKey.SqlState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Membership_foreign_keys_reject_unknown_users_and_teams(bool unknownUser)
    {
        var teams = await TeamsAsync();
        var note = await CreateNoteAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => Fixture.ExecuteAsync(
            "INSERT INTO team_memberships(user_id, team_id) VALUES (@user, @team)",
            new NpgsqlParameter("user", unknownUser ? Guid.NewGuid() : note.Note.OwnerId),
            new NpgsqlParameter("team", unknownUser ? teams.TeamA : Guid.NewGuid())));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task Owner_deletion_cascades_to_all_grants_in_PostgreSQL()
    {
        var teams = await TeamsAsync();
        var note = await CreateNoteAsync();
        using (var shared = await PutSharingAsync(Alice, note.Note.Id, note.ETag,
            new("team", teams.TeamA, "edit"),
            new("team", teams.TeamB, "read"),
            new("organization", null, "read")))
        {
            Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        }

        Assert.Equal(3, await GrantCountAsync(note.Note.Id));
        using (var deleted = await SendAsync(Alice, HttpMethod.Delete,
            $"/api/notes/{note.Note.Id}", etag: Tag(2)))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Empty(await deleted.Content.ReadAsByteArrayAsync());
        }

        Assert.Equal(0, await GrantCountAsync(note.Note.Id));
        await using var connection = await Fixture.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM notes WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", note.Note.Id);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    private Task<int> InsertGrantAsync(Guid noteId, string audience, Guid? teamId, string permission)
    {
        return Fixture.ExecuteAsync(
            """
            INSERT INTO note_grants(id, note_id, audience, team_id, permission)
            VALUES (@id, @note, @audience, @team, @permission)
            """,
            new NpgsqlParameter("id", Guid.NewGuid()),
            new NpgsqlParameter("note", noteId),
            new NpgsqlParameter("audience", audience),
            new NpgsqlParameter("team", NpgsqlDbType.Uuid) { Value = (object?)teamId ?? DBNull.Value },
            new NpgsqlParameter("permission", permission));
    }

    private async Task<long> GrantCountAsync(Guid noteId)
    {
        await using var connection = await Fixture.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM note_grants WHERE note_id = @id", connection);
        command.Parameters.AddWithValue("id", noteId);
        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }
}
