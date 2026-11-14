using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Notes.Api.Tests.Infrastructure;
using Npgsql;

namespace Notes.Api.Tests.Integration;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ConcurrencyTests(PostgreSqlFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Concurrent_content_saves_using_one_version_have_exactly_one_winner()
    {
        var note = await CreateNoteAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> SaveAsync(string title)
        {
            await start.Task;
            return await PutContentAsync(Alice, note.Note.Id, note.ETag, title, title);
        }

        var firstTask = SaveAsync("Concurrent first");
        var secondTask = SaveAsync("Concurrent second");
        start.SetResult();
        var responses = await Task.WhenAll(firstTask, secondTask);
        using var first = responses[0];
        using var second = responses[1];
        var winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.PreconditionFailed);
        await AssertProblemAsync(loser, HttpStatusCode.PreconditionFailed);
        var winningNote = await ReadNoteAsync(winner);
        var persisted = await GetNoteAsync(Alice, note.Note.Id);
        Assert.Equal(2, persisted.Note.Version);
        Assert.Equal(winningNote, persisted);
        Assert.Equal(persisted.Note.Title, persisted.Note.Body);
    }

    [Fact]
    public async Task Concurrent_sharing_replacements_have_one_winner_and_never_merge_grants()
    {
        var teams = await TeamsAsync();
        var note = await CreateNoteAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> ShareAsync(Guid team)
        {
            await start.Task;
            return await PutSharingAsync(Alice, note.Note.Id, note.ETag, new TestGrant("team", team, "read"));
        }

        var firstTask = ShareAsync(teams.TeamA);
        var secondTask = ShareAsync(teams.TeamB);
        start.SetResult();
        var responses = await Task.WhenAll(firstTask, secondTask);
        using var first = responses[0];
        using var second = responses[1];
        var winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.PreconditionFailed);
        await AssertProblemAsync(loser, HttpStatusCode.PreconditionFailed);
        Assert.Equal(Tag(2), winner.Headers.ETag?.Tag);
        var winningBody = await winner.Content.ReadAsStringAsync();
        Assert.Equal(winningBody, await Alice.GetStringAsync($"/api/notes/{note.Note.Id}/sharing"));
        using var json = JsonDocument.Parse(winningBody);
        Assert.Single(json.RootElement.GetProperty("grants").EnumerateArray());
        Assert.Equal(2, (await GetNoteAsync(Alice, note.Note.Id)).Note.Version);
    }

    [Fact]
    public async Task Editor_authorized_before_revocation_cannot_commit_after_revocation()
    {
        var teams = await TeamsAsync();
        var note = await CreateNoteAsync(body: "The body must survive revocation.");
        using (var response = await PutSharingAsync(Alice, note.Note.Id, note.ETag,
            new TestGrant("team", teams.TeamA, "edit")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var editorSnapshot = await GetNoteAsync(Bob, note.Note.Id);
        var race = await RunOrderedDatabaseRaceAsync(note.Note.Id,
            () => PutSharingAsync(Alice, note.Note.Id, editorSnapshot.ETag),
            () => PutContentAsync(Bob, note.Note.Id, editorSnapshot.ETag,
                "Revoked editor must not win", "Revoked editor must not win"));
        using var revoked = race.First;
        using var rejected = race.Second;
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(Tag(3), revoked.Headers.ETag?.Tag);
        await AssertProblemAsync(rejected, HttpStatusCode.PreconditionFailed);
        var current = await GetNoteAsync(Alice, note.Note.Id);
        Assert.Equal(3, current.Note.Version);
        Assert.Equal(note.Note.Body, current.Note.Body);
        Assert.Equal(note.Note.Title, current.Note.Title);
        using var sharing = JsonDocument.Parse(await Alice.GetStringAsync($"/api/notes/{note.Note.Id}/sharing"));
        Assert.Empty(sharing.RootElement.GetProperty("grants").EnumerateArray());
        using var denied = await Bob.GetAsync($"/api/notes/{note.Note.Id}");
        await AssertProblemAsync(denied, HttpStatusCode.NotFound, noVersion: true);
    }

    [Fact]
    public async Task Deletion_authorized_at_an_old_version_cannot_delete_a_concurrently_updated_note()
    {
        var note = await CreateNoteAsync();
        var race = await RunOrderedDatabaseRaceAsync(note.Note.Id,
            () => PutContentAsync(Alice, note.Note.Id, note.ETag, "Concurrent edit survives"),
            () => SendAsync(Alice, HttpMethod.Delete, $"/api/notes/{note.Note.Id}", etag: note.ETag));
        using var saved = race.First;
        using var staleDelete = race.Second;
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        await AssertProblemAsync(staleDelete, HttpStatusCode.PreconditionFailed);
        var current = await GetNoteAsync(Alice, note.Note.Id);
        Assert.Equal(await ReadNoteAsync(saved), current);
        Assert.Equal(2, current.Note.Version);
        Assert.Equal("Concurrent edit survives", current.Note.Title);
    }

    private async Task<(HttpResponseMessage First, HttpResponseMessage Second)> RunOrderedDatabaseRaceAsync(
        Guid noteId,
        Func<Task<HttpResponseMessage>> startFirst,
        Func<Task<HttpResponseMessage>> startSecond)
    {
        await using var blocker = await Fixture.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var lockRow = new NpgsqlCommand(
            "SELECT id FROM notes WHERE id = @id FOR UPDATE", blocker, transaction))
        {
            lockRow.Parameters.AddWithValue("id", noteId);
            Assert.Equal(noteId, await lockRow.ExecuteScalarAsync());
        }

        Task<HttpResponseMessage>? firstTask = null;
        Task<HttpResponseMessage>? secondTask = null;
        bool firstWasBlocked;
        bool bothWereBlocked;
        try
        {
            // The first mutation queues first; both must reach their database CAS before release.
            firstTask = startFirst();
            firstWasBlocked = await WaitForBlockedWritesAsync(1);
            secondTask = startSecond();
            bothWereBlocked = await WaitForBlockedWritesAsync(2);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            if (firstTask is not null)
            {
                (await firstTask).Dispose();
            }

            if (secondTask is not null)
            {
                (await secondTask).Dispose();
            }

            throw;
        }

        var first = await firstTask;
        var second = await secondTask;
        try
        {
            Assert.True(firstWasBlocked, "The first mutation never reached a blocked database write.");
            Assert.True(bothWereBlocked, "Both authorized mutations must be observed waiting on database locks.");
            return (first, second);
        }
        catch
        {
            first.Dispose();
            second.Dispose();
            throw;
        }
    }

    private async Task<bool> WaitForBlockedWritesAsync(int expected)
    {
        var timer = Stopwatch.StartNew();
        await using var connection = await Fixture.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM pg_stat_activity
            WHERE datname = current_database()
              AND application_name = @application
              AND state = 'active'
              AND wait_event_type = 'Lock'
              AND query ~* '(UPDATE|DELETE[[:space:]]+FROM)[[:space:]]+"?notes"?'
            """, connection);
        command.Parameters.AddWithValue("application", Fixture.ApplicationName);
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) >= expected)
            {
                return true;
            }

            // Poll an observed lock condition, rather than assuming a request is ready after a sleep.
            await Task.Delay(20);
        }

        return false;
    }
}
