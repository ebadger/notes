using System.Net;
using System.Text.Json;
using Notes.Api.Tests.Infrastructure;

namespace Notes.Api.Tests.Integration;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SharingValidationTests(PostgreSqlFixture fixture) : ApiTestBase(fixture)
{
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("""{"grants":null}""")]
    [InlineData("""{"grants":{}}""")]
    [InlineData("""{"grants":[null]}""")]
    [InlineData("""{"grants":["read"]}""")]
    [InlineData("""{"grants":[{}]}""")]
    [InlineData("""{"grants":[{"audience":"organization"}]}""")]
    [InlineData("""{"grants":[{"permission":"read"}]}""")]
    [InlineData("""{"grants":[{"audience":null,"permission":"read"}]}""")]
    [InlineData("""{"grants":[{"audience":"organization","permission":null}]}""")]
    [InlineData("""{"grants":[{"audience":"public","permission":"read"}]}""")]
    [InlineData("""{"grants":[{"audience":"organization","permission":"owner"}]}""")]
    [InlineData("""{"grants":[{"audience":1,"permission":"read"}]}""")]
    [InlineData("""{"grants":[{"audience":"organization","permission":0}]}""")]
    [InlineData("""{"grants":[{"audience":"team","permission":"read"}]}""")]
    [InlineData("""{"grants":[{"audience":"team","teamId":null,"permission":"edit"}]}""")]
    [InlineData("""{"grants":[{"audience":"team","teamId":"not-a-guid","permission":"edit"}]}""")]
    [InlineData("""{"grants":[{"audience":"team","teamId":"00000000-0000-0000-0000-000000000000","permission":"read"}]}""")]
    [InlineData("""{"grants":[],"ownerId":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"grants":[{"audience":"organization","permission":"read","unexpected":true}]}""")]
    public async Task Invalid_sharing_requests_leave_existing_grants_and_version_unchanged(string json)
    {
        await AssertRejectedAtomicallyAsync(json);
    }

    [Theory]
    [InlineData("organization-with-team")]
    [InlineData("duplicate-team")]
    [InlineData("duplicate-organization")]
    [InlineData("unknown-team-after-valid-grant")]
    public async Task Invalid_target_combinations_and_duplicates_are_atomic(string scenario)
    {
        var teams = await TeamsAsync();
        TestGrant[] grants = scenario switch
        {
            "organization-with-team" => [new("organization", teams.TeamA, "read")],
            "duplicate-team" =>
            [
                new("team", teams.TeamA, "read"),
                new("team", teams.TeamA, "edit")
            ],
            "duplicate-organization" =>
            [
                new("organization", null, "read"),
                new("organization", null, "edit")
            ],
            "unknown-team-after-valid-grant" =>
            [
                new("team", teams.TeamB, "edit"),
                new("team", Guid.NewGuid(), "read")
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        await AssertRejectedAtomicallyAsync(JsonSerializer.Serialize(new { grants }, JsonOptions));
    }

    [Fact]
    public async Task Replacement_is_complete_and_each_success_increments_the_shared_version_once()
    {
        var teams = await TeamsAsync();
        var note = await CreateNoteAsync();
        using (var first = await PutSharingAsync(Alice, note.Note.Id, note.ETag,
            new("team", teams.TeamA, "edit"), new("organization", null, "read")))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(Tag(2), first.Headers.ETag?.Tag);
            AssertNoStore(first);
        }

        var before = await GetNoteAsync(Alice, note.Note.Id);
        using (var replacement = await PutSharingAsync(Alice, note.Note.Id, before.ETag,
            new TestGrant("team", teams.TeamB, "read")))
        {
            Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
            Assert.Equal(Tag(3), replacement.Headers.ETag?.Tag);
            using var json = JsonDocument.Parse(await replacement.Content.ReadAsStringAsync());
            var grant = Assert.Single(json.RootElement.GetProperty("grants").EnumerateArray());
            Assert.Equal("team", grant.GetProperty("audience").GetString());
            Assert.Equal("read", grant.GetProperty("permission").GetString());
            Assert.Equal(teams.TeamB, grant.GetProperty("teamId").GetGuid());
        }

        var after = await GetNoteAsync(Alice, note.Note.Id);
        Assert.Equal(3, after.Note.Version);
        Assert.Equal(note.Note.CreatedAt, after.Note.CreatedAt);
        Assert.True(after.Note.UpdatedAt > before.Note.UpdatedAt);
        Assert.Equal(note.Note.Body, after.Note.Body);
        Assert.Equal(note.Note.Title, after.Note.Title);
        Assert.Equal(Tag(3), await ReadSharingTagAsync(Alice, note.Note.Id));
        using var bob = await Bob.GetAsync($"/api/notes/{note.Note.Id}");
        await AssertProblemAsync(bob, HttpStatusCode.NotFound, noVersion: true);
        await GetNoteAsync(Carol, note.Note.Id);

        using var sameAgain = await PutSharingAsync(Alice, note.Note.Id, after.ETag,
            new TestGrant("team", teams.TeamB, "read"));
        Assert.Equal(HttpStatusCode.OK, sameAgain.StatusCode);
        Assert.Equal(Tag(4), sameAgain.Headers.ETag?.Tag);
    }

    private async Task AssertRejectedAtomicallyAsync(string json)
    {
        var teams = await TeamsAsync();
        var note = await CreateNoteAsync();
        using (var shared = await PutSharingAsync(Alice, note.Note.Id, note.ETag,
            new("team", teams.TeamA, "edit"), new("organization", null, "read")))
        {
            Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        }

        var before = await GetNoteAsync(Alice, note.Note.Id);
        var sharingBefore = await Alice.GetStringAsync($"/api/notes/{note.Note.Id}/sharing");
        using var response = await SendAsync(Alice, HttpMethod.Put,
            $"/api/notes/{note.Note.Id}/sharing", json, before.ETag);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(before, await GetNoteAsync(Alice, note.Note.Id));
        Assert.Equal(sharingBefore, await Alice.GetStringAsync($"/api/notes/{note.Note.Id}/sharing"));
        Assert.Equal(before.ETag, await ReadSharingTagAsync(Alice, note.Note.Id));
        await GetNoteAsync(Bob, note.Note.Id);
        await GetNoteAsync(Carol, note.Note.Id);
    }
}
