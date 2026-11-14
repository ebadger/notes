using System.Net;
using Notes.Api.Tests.Infrastructure;

namespace Notes.Api.Tests.Integration;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PreconditionTests(PostgreSqlFixture fixture) : ApiTestBase(fixture)
{
    [Theory]
    [InlineData(null, 428)]
    [InlineData("\"2\"", 412)]
    [InlineData("\"9223372036854775807\"", 412)]
    [InlineData("\"0\"", 400)]
    [InlineData("\"01\"", 400)]
    [InlineData("\"0001\"", 400)]
    [InlineData("\"-1\"", 400)]
    [InlineData("\"+1\"", 400)]
    [InlineData("1", 400)]
    [InlineData("W/\"1\"", 400)]
    [InlineData("*", 400)]
    [InlineData("\"1\", \"2\"", 400)]
    [InlineData("\"1\", \"1\"", 400)]
    [InlineData("\"\"", 400)]
    [InlineData("\"1.0\"", 400)]
    [InlineData("\"9223372036854775808\"", 400)]
    public async Task All_mutations_enforce_exact_precondition_statuses_without_changing_state(
        string? etag,
        int status)
    {
        var note = await CreateNoteAsync();
        foreach (var (method, suffix, content) in Mutations())
        {
            using var response = await SendAsync(Alice, method,
                $"/api/notes/{note.Note.Id}{suffix}", content, etag);
            await AssertProblemAsync(response, (HttpStatusCode)status);
            Assert.Equal(note, await GetNoteAsync(Alice, note.Note.Id));
            Assert.Equal(note.ETag, await ReadSharingTagAsync(Alice, note.Note.Id));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("malformed")]
    [InlineData("\"999\"")]
    public async Task Authorization_precedes_preconditions_and_never_discloses_hidden_versions(string? etag)
    {
        var teams = await TeamsAsync();
        var note = await CreateNoteAsync();
        using (var shared = await PutSharingAsync(Alice, note.Note.Id, note.ETag,
            new TestGrant("team", teams.TeamB, "read")))
        {
            Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        }

        foreach (var (method, suffix, content) in Mutations())
        {
            using (var hidden = await SendAsync(Bob, method,
                $"/api/notes/{note.Note.Id}{suffix}", content, etag))
            {
                await AssertProblemAsync(hidden, HttpStatusCode.NotFound, noVersion: true);
            }

            using (var forbidden = await SendAsync(Carol, method,
                $"/api/notes/{note.Note.Id}{suffix}", content, etag))
            {
                await AssertProblemAsync(forbidden, HttpStatusCode.Forbidden, noVersion: true);
            }

            using var absent = await SendAsync(Alice, method,
                $"/api/notes/{Guid.NewGuid()}{suffix}", content, etag);
            await AssertProblemAsync(absent, HttpStatusCode.NotFound, noVersion: true);
        }
    }

    [Fact]
    public async Task Multiple_if_match_header_values_are_rejected()
    {
        var note = await CreateNoteAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/notes/{note.Note.Id}")
        {
            Content = Json("""{"title":"New title","body":""}""")
        };
        request.Headers.TryAddWithoutValidation("If-Match", new[] { note.ETag, note.ETag });
        using var response = await Alice.SendAsync(request);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(note, await GetNoteAsync(Alice, note.Note.Id));
    }

    [Fact]
    public async Task Content_and_sharing_invalidate_each_others_previous_version()
    {
        var note = await CreateNoteAsync();
        using (var share = await PutSharingAsync(Alice, note.Note.Id, note.ETag,
            new TestGrant("organization", null, "read")))
        {
            Assert.Equal(HttpStatusCode.OK, share.StatusCode);
            Assert.Equal(Tag(2), share.Headers.ETag?.Tag);
        }

        using (var staleContent = await PutContentAsync(Alice, note.Note.Id, note.ETag))
        {
            await AssertProblemAsync(staleContent, HttpStatusCode.PreconditionFailed);
        }

        using (var content = await PutContentAsync(Alice, note.Note.Id, Tag(2)))
        {
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            Assert.Equal(Tag(3), content.Headers.ETag?.Tag);
        }

        using (var staleSharing = await PutSharingAsync(Alice, note.Note.Id, Tag(2)))
        {
            await AssertProblemAsync(staleSharing, HttpStatusCode.PreconditionFailed);
        }

        using (var staleDelete = await SendAsync(Alice, HttpMethod.Delete,
            $"/api/notes/{note.Note.Id}", etag: Tag(2)))
        {
            await AssertProblemAsync(staleDelete, HttpStatusCode.PreconditionFailed);
        }

        Assert.Equal(Tag(3), (await GetNoteAsync(Alice, note.Note.Id)).ETag);
        await GetNoteAsync(Carol, note.Note.Id);
    }

    private static IEnumerable<(HttpMethod Method, string Suffix, string? Content)> Mutations()
    {
        yield return (HttpMethod.Put, "", """{"title":"Attempted edit","body":""}""");
        yield return (HttpMethod.Put, "/sharing", """{"grants":[]}""");
        yield return (HttpMethod.Delete, "", null);
    }
}
