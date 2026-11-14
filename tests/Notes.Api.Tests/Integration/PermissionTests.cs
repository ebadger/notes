using System.Net;
using System.Text.Json;
using Notes.Api.Tests.Infrastructure;

namespace Notes.Api.Tests.Integration;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PermissionTests(PostgreSqlFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task New_notes_are_private_and_all_hidden_resource_routes_are_nondisclosing()
    {
        var created = await CreateNoteAsync(title: $"Private secret {Guid.NewGuid():N}");
        var read = await GetNoteAsync(Alice, created.Note.Id);
        Assert.Equal(created, read);
        using (var sharing = await Alice.GetAsync($"/api/notes/{created.Note.Id}/sharing"))
        {
            Assert.Equal(HttpStatusCode.OK, sharing.StatusCode);
            using var json = JsonDocument.Parse(await sharing.Content.ReadAsStringAsync());
            Assert.Empty(json.RootElement.GetProperty("grants").EnumerateArray());
        }

        foreach (var client in new[] { Bob, Carol, Dana })
        {
            foreach (var id in new[] { created.Note.Id, Guid.NewGuid() })
            {
                foreach (var (method, suffix, content) in ResourceOperations())
                {
                    using var response = await SendAsync(client, method, $"/api/notes/{id}{suffix}", content, "\"999\"");
                    await AssertProblemAsync(response, HttpStatusCode.NotFound, noVersion: true);
                    var error = await response.Content.ReadAsStringAsync();
                    Assert.DoesNotContain(created.Note.Title, error);
                    Assert.DoesNotContain(created.Note.Body, error);
                    Assert.DoesNotContain(created.Note.OwnerId.ToString(), error);
                }
            }

            Assert.DoesNotContain(await AllNotesAsync(client), item => item.Id == created.Note.Id);
        }

        Assert.Contains(await AllNotesAsync(Alice), item => item.Id == created.Note.Id);
    }

    [Fact]
    public async Task Team_editor_and_organization_reader_have_only_their_allowed_operations()
    {
        var teams = await TeamsAsync();
        var created = await CreateNoteAsync();
        using (var response = await PutSharingAsync(Alice, created.Note.Id, created.ETag,
            new("team", teams.TeamA, "edit"), new("organization", null, "read")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Tag(2), response.Headers.ETag?.Tag);
        }

        var editorRead = await GetNoteAsync(Bob, created.Note.Id);
        var readerRead = await GetNoteAsync(Carol, created.Note.Id);
        Assert.Equal(editorRead, readerRead);
        using (var edit = await PutContentAsync(Bob, created.Note.Id, editorRead.ETag))
        {
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            var changed = await ReadNoteAsync(edit);
            Assert.Equal(created.Note.OwnerId, changed.Note.OwnerId);
            Assert.Equal(3, changed.Note.Version);
        }

        foreach (var client in new[] { Bob, Carol })
        {
            foreach (var (method, suffix, content) in ResourceOperations().Where(operation =>
                operation.Method == HttpMethod.Delete || operation.Suffix == "/sharing"))
            {
                using var response = await SendAsync(client, method,
                    $"/api/notes/{created.Note.Id}{suffix}", content, Tag(3));
                await AssertProblemAsync(response, HttpStatusCode.Forbidden, noVersion: true);
            }
        }

        using (var denied = await PutContentAsync(Carol, created.Note.Id, Tag(3)))
        {
            await AssertProblemAsync(denied, HttpStatusCode.Forbidden, noVersion: true);
        }

        using (var ownerEdit = await PutContentAsync(Alice, created.Note.Id, Tag(3)))
        {
            Assert.Equal(HttpStatusCode.OK, ownerEdit.StatusCode);
        }

        using var deleted = await SendAsync(Alice, HttpMethod.Delete, $"/api/notes/{created.Note.Id}", etag: Tag(4));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        AssertNoStore(deleted);
        using var missing = await Alice.GetAsync($"/api/notes/{created.Note.Id}");
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, noVersion: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multiple_matching_grants_choose_strongest_access_without_duplicate_list_items(bool reverse)
    {
        var teams = await TeamsAsync();
        var created = await CreateNoteAsync();
        TestGrant[] grants =
        [
            new("team", teams.TeamA, "read"),
            new("team", teams.TeamB, "edit"),
            new("organization", null, "read")
        ];
        if (reverse)
        {
            Array.Reverse(grants);
        }

        using (var shared = await PutSharingAsync(Alice, created.Note.Id, created.ETag, grants))
        {
            Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        }

        var danaRead = await GetNoteAsync(Dana, created.Note.Id);
        Assert.Single(await AllNotesAsync(Dana), item => item.Id == created.Note.Id);
        using var edited = await PutContentAsync(Dana, created.Note.Id, danaRead.ETag);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        using var denied = await PutContentAsync(Bob, created.Note.Id, Tag(3));
        await AssertProblemAsync(denied, HttpStatusCode.Forbidden, noVersion: true);
    }

    [Fact]
    public async Task Owner_can_share_with_a_team_the_owner_does_not_belong_to()
    {
        var teams = await TeamsAsync();
        var created = await CreateNoteAsync();
        using var response = await PutSharingAsync(Alice, created.Note.Id, created.ETag,
            new TestGrant("team", teams.TeamB, "edit"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await GetNoteAsync(Carol, created.Note.Id);
        using var hidden = await Bob.GetAsync($"/api/notes/{created.Note.Id}");
        await AssertProblemAsync(hidden, HttpStatusCode.NotFound, noVersion: true);
        using var ownerEdit = await PutContentAsync(Alice, created.Note.Id, Tag(2));
        Assert.Equal(HttpStatusCode.OK, ownerEdit.StatusCode);
    }

    [Fact]
    public async Task Replacing_grants_with_empty_revokes_read_edit_and_list_access()
    {
        var created = await CreateNoteAsync();
        using (var shared = await PutSharingAsync(Alice, created.Note.Id, created.ETag,
            new TestGrant("organization", null, "edit")))
        {
            Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        }

        var before = await GetNoteAsync(Bob, created.Note.Id);
        using (var revoked = await PutSharingAsync(Alice, created.Note.Id, before.ETag))
        {
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
            Assert.Equal(Tag(3), revoked.Headers.ETag?.Tag);
            using var json = JsonDocument.Parse(await revoked.Content.ReadAsStringAsync());
            Assert.Empty(json.RootElement.GetProperty("grants").EnumerateArray());
        }

        foreach (var client in new[] { Bob, Carol, Dana })
        {
            using var hidden = await client.GetAsync($"/api/notes/{created.Note.Id}");
            await AssertProblemAsync(hidden, HttpStatusCode.NotFound, noVersion: true);
            using var edit = await PutContentAsync(client, created.Note.Id, before.ETag);
            await AssertProblemAsync(edit, HttpStatusCode.NotFound, noVersion: true);
            Assert.DoesNotContain(await AllNotesAsync(client), item => item.Id == created.Note.Id);
        }

        var ownerRead = await GetNoteAsync(Alice, created.Note.Id);
        Assert.Equal(3, ownerRead.Note.Version);
        Assert.Equal(created.Note.OwnerId, ownerRead.Note.OwnerId);
    }

    private static IEnumerable<(HttpMethod Method, string Suffix, string? Content)> ResourceOperations()
    {
        yield return (HttpMethod.Get, "", null);
        yield return (HttpMethod.Put, "", """{"title":"Attempted edit","body":""}""");
        yield return (HttpMethod.Delete, "", null);
        yield return (HttpMethod.Get, "/sharing", null);
        yield return (HttpMethod.Put, "/sharing", """{"grants":[]}""");
    }
}
