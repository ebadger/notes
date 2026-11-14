using System.Net;
using System.Text;
using System.Text.Json;
using Notes.Api.Tests.Infrastructure;

namespace Notes.Api.Tests.Integration;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ContentContractTests(PostgreSqlFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Content_is_trimmed_only_in_title_and_is_not_rendered_or_interpreted()
    {
        const string body = " \r\n# Markdown\n<script>alert('plain text')</script>\t ";
        var created = await CreateNoteAsync(title: "\u2003 \tA title\r\n ", body: body);
        Assert.Equal("A title", created.Note.Title);
        Assert.Equal(body, created.Note.Body);
        Assert.Equal(created.Note.CreatedAt, created.Note.UpdatedAt);
        Assert.Equal(created, await GetNoteAsync(Alice, created.Note.Id));

        using var response = await PutContentAsync(Alice, created.Note.Id, created.ETag,
            "  Revised title \t", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var revised = await ReadNoteAsync(response);
        Assert.Equal("Revised title", revised.Note.Title);
        Assert.Equal(body, revised.Note.Body);
        Assert.Equal(created.Note.CreatedAt, revised.Note.CreatedAt);
        Assert.True(revised.Note.UpdatedAt > created.Note.UpdatedAt);
        Assert.Equal(2, revised.Note.Version);
    }

    [Theory]
    [InlineData(false, 1, true)]
    [InlineData(false, 200, true)]
    [InlineData(false, 201, false)]
    [InlineData(true, 1, true)]
    [InlineData(true, 200, true)]
    [InlineData(true, 201, false)]
    public async Task Trimmed_title_boundary_is_enforced_on_create_and_update(bool update, int length, bool valid)
    {
        var json = JsonSerializer.Serialize(new { title = $" \t{new string('x', length)} \r\n", body = "" });
        using var response = await WriteAsync(update, json);
        if (valid)
        {
            Assert.Equal(update ? HttpStatusCode.OK : HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(length, (await ReadNoteAsync(response)).Note.Title.Length);
        }
        else
        {
            await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        }
    }

    [Theory]
    [InlineData(false, 100, true)]
    [InlineData(false, 101, false)]
    [InlineData(true, 100, true)]
    [InlineData(true, 101, false)]
    public async Task Title_boundary_counts_utf16_units(bool update, int symbols, bool valid)
    {
        var title = string.Concat(Enumerable.Repeat("\U0001f642", symbols));
        Assert.Equal(symbols * 2, title.Length);
        using var response = await WriteAsync(update, JsonSerializer.Serialize(new { title, body = "" }));
        if (valid)
        {
            Assert.Equal(update ? HttpStatusCode.OK : HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(title, (await ReadNoteAsync(response)).Note.Title);
        }
        else
        {
            await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        }
    }

    [Theory]
    [InlineData(false, false, 0, true)]
    [InlineData(false, false, 65536, true)]
    [InlineData(false, false, 65537, false)]
    [InlineData(true, false, 65536, true)]
    [InlineData(true, false, 65537, false)]
    [InlineData(false, true, 65536, true)]
    [InlineData(false, true, 65537, false)]
    [InlineData(true, true, 65536, true)]
    [InlineData(true, true, 65537, false)]
    public async Task Decoded_body_utf8_boundary_is_exact(
        bool update,
        bool multibyte,
        int bytes,
        bool valid)
    {
        var body = multibyte
            ? string.Concat(Enumerable.Repeat("\U0001f642", bytes / 4)) + new string('x', bytes % 4)
            : new string('x', bytes);
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(body));
        using var response = await WriteAsync(update, JsonSerializer.Serialize(new { title = "Boundary", body }));
        if (valid)
        {
            Assert.Equal(update ? HttpStatusCode.OK : HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(body, (await ReadNoteAsync(response)).Note.Body);
        }
        else
        {
            await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"title":"Title"}""")]
    [InlineData("""{"body":""}""")]
    [InlineData("""{"title":null,"body":""}""")]
    [InlineData("""{"title":"Title","body":null}""")]
    [InlineData("""{"title":"","body":""}""")]
    [InlineData("""{"title":" \r\n\t","body":""}""")]
    [InlineData("""{"title":123,"body":""}""")]
    [InlineData("""{"title":"Title","body":123}""")]
    [InlineData("""{"title":"Title","body":{}}""")]
    [InlineData("""{"title":"Title","body":[]}""")]
    [InlineData("""{"title":"a\u0000b","body":""}""")]
    [InlineData("""{"title":"Title","body":"a\u0000b"}""")]
    [InlineData("""{"title":"\ud800","body":""}""")]
    [InlineData("""{"title":"Title","body":"\udc00"}""")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    public async Task Invalid_content_is_rejected_without_mutation_on_both_routes(string json)
    {
        using (var create = await SendAsync(Alice, HttpMethod.Post, "/api/notes", json))
        {
            await AssertProblemAsync(create, HttpStatusCode.BadRequest);
        }

        var original = await CreateNoteAsync();
        using var update = await SendAsync(Alice, HttpMethod.Put,
            $"/api/notes/{original.Note.Id}", json, original.ETag);
        await AssertProblemAsync(update, HttpStatusCode.BadRequest);
        Assert.Equal(original, await GetNoteAsync(Alice, original.Note.Id));
    }

    [Theory]
    [InlineData("ownerId", "\"22222222-2222-2222-2222-222222222222\"")]
    [InlineData("grants", "[]")]
    [InlineData("version", "100")]
    [InlineData("id", "\"00000000-0000-0000-0000-000000000001\"")]
    [InlineData("createdAt", "\"2000-01-01T00:00:00Z\"")]
    [InlineData("unknown", "true")]
    public async Task Request_fields_cannot_change_ownership_sharing_or_server_metadata(string name, string value)
    {
        var json = $$"""{"title":"Injected","body":"","{{name}}":{{value}}}""";
        using (var create = await SendAsync(Alice, HttpMethod.Post, "/api/notes", json))
        {
            await AssertProblemAsync(create, HttpStatusCode.BadRequest);
        }

        var teams = await TeamsAsync();
        var original = await CreateNoteAsync();
        using (var shared = await PutSharingAsync(Alice, original.Note.Id, original.ETag,
            new TestGrant("team", teams.TeamA, "edit")))
        {
            Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        }

        var before = await GetNoteAsync(Alice, original.Note.Id);
        using var response = await SendAsync(Bob, HttpMethod.Put,
            $"/api/notes/{original.Note.Id}", json, before.ETag);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(before, await GetNoteAsync(Alice, original.Note.Id));
        Assert.Equal(before.ETag, await ReadSharingTagAsync(Alice, original.Note.Id));
        await GetNoteAsync(Bob, original.Note.Id);
    }

    [Fact]
    public async Task Legal_editor_content_update_preserves_ownership_and_exact_grants()
    {
        var teams = await TeamsAsync();
        var original = await CreateNoteAsync();
        using (var shared = await PutSharingAsync(Alice, original.Note.Id, original.ETag,
            new("team", teams.TeamA, "edit"), new("organization", null, "read")))
        {
            Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        }

        var before = await Alice.GetStringAsync($"/api/notes/{original.Note.Id}/sharing");
        using (var update = await PutContentAsync(Bob, original.Note.Id, Tag(2)))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            var note = await ReadNoteAsync(update);
            Assert.Equal(original.Note.OwnerId, note.Note.OwnerId);
            Assert.Equal(original.Note.CreatedAt, note.Note.CreatedAt);
        }

        Assert.Equal(before, await Alice.GetStringAsync($"/api/notes/{original.Note.Id}/sharing"));
        Assert.Equal(Tag(3), await ReadSharingTagAsync(Alice, original.Note.Id));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("123")]
    [InlineData("11111111-1111-1111-1111-11111111111z")]
    public async Task Invalid_route_ids_return_problem_details(string id)
    {
        foreach (var (method, suffix, content) in new[]
        {
            (HttpMethod.Get, "", (string?)null),
            (HttpMethod.Put, "", """{"title":"Title","body":""}"""),
            (HttpMethod.Delete, "", (string?)null),
            (HttpMethod.Get, "/sharing", (string?)null),
            (HttpMethod.Put, "/sharing", """{"grants":[]}""")
        })
        {
            using var response = await SendAsync(Alice, method, $"/api/notes/{id}{suffix}", content, Tag(1));
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, noVersion: true);
        }
    }

    [Fact]
    public async Task Unexpected_database_errors_return_sanitized_problem_details_with_a_trace_id()
    {
        var note = await CreateNoteAsync(title: $"Confidential {Guid.NewGuid():N}",
            body: $"Confidential body {Guid.NewGuid():N}");
        await Fixture.ExecuteAsync("ALTER TABLE notes RENAME COLUMN body TO test_unavailable_body");
        try
        {
            using var response = await Alice.GetAsync($"/api/notes/{note.Note.Id}");
            await AssertProblemAsync(response, HttpStatusCode.InternalServerError, noVersion: true);
            var text = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(text);
            Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
            Assert.DoesNotContain(note.Note.Title, text);
            Assert.DoesNotContain(note.Note.Body, text);
            Assert.DoesNotContain(Fixture.ApiKey(TestUser.Alice), text);
            Assert.DoesNotContain("Npgsql", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("42703", text);
            Assert.DoesNotContain("test_unavailable_body", text);
            Assert.False(json.RootElement.TryGetProperty("stackTrace", out _));
            Assert.False(json.RootElement.TryGetProperty("exception", out _));
        }
        finally
        {
            await Fixture.ExecuteAsync("ALTER TABLE notes RENAME COLUMN test_unavailable_body TO body");
        }

        Assert.Equal(note, await GetNoteAsync(Alice, note.Note.Id));
    }

    private async Task<HttpResponseMessage> WriteAsync(bool update, string json)
    {
        if (!update)
        {
            return await SendAsync(Alice, HttpMethod.Post, "/api/notes", json);
        }

        var note = await CreateNoteAsync();
        return await SendAsync(Alice, HttpMethod.Put, $"/api/notes/{note.Note.Id}", json, note.ETag);
    }
}
