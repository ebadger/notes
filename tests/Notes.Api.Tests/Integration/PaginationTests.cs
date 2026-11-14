using System.Net;
using System.Text.Json;
using Notes.Api.Tests.Infrastructure;
using Npgsql;

namespace Notes.Api.Tests.Integration;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PaginationTests(PostgreSqlFixture fixture) : ApiTestBase(fixture)
{
    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=-1")]
    [InlineData("limit=101")]
    [InlineData("limit=abc")]
    [InlineData("limit=1.5")]
    [InlineData("limit=2147483648")]
    [InlineData("limit=")]
    [InlineData("offset=-1")]
    [InlineData("offset=abc")]
    [InlineData("offset=1.5")]
    [InlineData("offset=2147483648")]
    [InlineData("offset=")]
    public async Task Invalid_pagination_returns_problem_details(string query)
    {
        using var response = await Alice.GetAsync($"/api/notes?{query}");
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Default_page_size_and_end_of_collection_shape_are_exact()
    {
        await Fixture.ExecuteAsync("DELETE FROM notes");
        for (var index = 0; index < 21; index++)
        {
            await CreateNoteAsync(title: $"Default pagination {index}");
        }

        using var response = await Alice.GetAsync("/api/notes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(20, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(20, json.RootElement.GetProperty("nextOffset").GetInt32());
        var last = await ListAsync(Alice, 20, 20);
        Assert.Single(last.Items);
        Assert.Null(last.NextOffset);
        var beyond = await ListAsync(Alice, 100, int.MaxValue);
        Assert.Empty(beyond.Items);
        Assert.Null(beyond.NextOffset);
    }

    [Fact]
    public async Task Tied_timestamps_use_descending_uuid_order_and_repeat_stably()
    {
        await Fixture.ExecuteAsync("DELETE FROM notes");
        List<Guid> ids = [];
        for (var index = 0; index < 5; index++)
        {
            ids.Add((await CreateNoteAsync()).Note.Id);
        }

        var tiedTime = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await Fixture.ExecuteAsync(
            "UPDATE notes SET created_at = @at, updated_at = @at WHERE id = ANY(@ids)",
            new NpgsqlParameter("at", tiedTime), new NpgsqlParameter("ids", ids.ToArray()));
        var expected = ids.OrderByDescending(id => id.ToString("N"), StringComparer.Ordinal).ToArray();
        for (var pass = 0; pass < 2; pass++)
        {
            List<Guid> actual = [];
            int? offset = 0;
            while (offset is not null)
            {
                var page = await ListAsync(Alice, 2, offset.Value);
                actual.AddRange(page.Items.Select(item => item.Id));
                Assert.All(page.Items, item => Assert.Equal(tiedTime, item.CreatedAt.UtcDateTime));
                offset = page.NextOffset;
            }

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task Access_filtering_and_deduplication_happen_before_pagination()
    {
        await Fixture.ExecuteAsync("DELETE FROM notes");
        var teams = await TeamsAsync();
        List<Guid> accessible = [];
        for (var index = 0; index < 3; index++)
        {
            accessible.Add((await CreateNoteAsync(Bob, $"Bob visible {index}")).Note.Id);
            await CreateNoteAsync(Alice, $"Alice hidden {index}");
        }

        var shared = await CreateNoteAsync();
        accessible.Add(shared.Note.Id);
        using (var response = await PutSharingAsync(Alice, shared.Note.Id, shared.ETag,
            new("team", teams.TeamA, "read"), new("organization", null, "read")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        for (var index = 0; index < 5; index++)
        {
            await CreateNoteAsync(Alice, $"Newer inaccessible {index}");
        }

        var first = await ListAsync(Bob, 2, 0);
        using var capture = new SqlCapture();
        var second = await ListAsync(Bob, 2, 2);
        Assert.Null(capture.Error);
        var query = Assert.Single(capture.Commands, sql =>
            (sql.Contains("FROM notes", StringComparison.OrdinalIgnoreCase) ||
             sql.Contains("FROM \"notes\"", StringComparison.OrdinalIgnoreCase)) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("WHERE", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("note_grants", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("team_memberships", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, first.NextOffset);
        Assert.Equal(2, second.Items.Count);
        Assert.Null(second.NextOffset);
        var returned = first.Items.Concat(second.Items).Select(item => item.Id).ToArray();
        Assert.Equal(accessible.Order(), returned.Order());
        Assert.Equal(returned.Length, returned.Distinct().Count());
        var last = await ListAsync(Bob, 1, 4);
        Assert.Empty(last.Items);
        Assert.Null(last.NextOffset);
    }
}
