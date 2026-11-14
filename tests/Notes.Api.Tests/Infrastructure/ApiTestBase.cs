using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Notes.Api.Tests.Infrastructure;

public abstract class ApiTestBase(PostgreSqlFixture fixture)
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected PostgreSqlFixture Fixture { get; } = fixture;

    protected HttpClient Alice => Fixture.Client(TestUser.Alice);
    protected HttpClient Bob => Fixture.Client(TestUser.Bob);
    protected HttpClient Carol => Fixture.Client(TestUser.Carol);
    protected HttpClient Dana => Fixture.Client(TestUser.Dana);

    protected static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    protected static string Tag(long version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    protected async Task<NoteResult> CreateNoteAsync(
        HttpClient? client = null,
        string? title = null,
        string body = "A test-only Markdown body.")
    {
        using var response = await (client ?? Alice).PostAsJsonAsync("/api/notes", new
        {
            title = title ?? $"Test {Guid.NewGuid():N}",
            body
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await ReadNoteAsync(response);
        var location = Assert.IsType<Uri>(response.Headers.Location);
        Assert.Equal($"/api/notes/{result.Note.Id}",
            location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString);
        Assert.Equal(1, result.Note.Version);
        return result;
    }

    protected static async Task<NoteResult> GetNoteAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"/api/notes/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadNoteAsync(response);
    }

    protected static async Task<NoteResult> ReadNoteAsync(HttpResponseMessage response)
    {
        AssertNoStore(response);
        var note = await response.Content.ReadFromJsonAsync<TestNote>();
        Assert.NotNull(note);
        Assert.NotEqual(Guid.Empty, note.Id);
        Assert.NotEqual(Guid.Empty, note.OwnerId);
        Assert.NotNull(note.Title);
        Assert.NotNull(note.Body);
        Assert.NotEqual(default, note.CreatedAt);
        Assert.True(note.UpdatedAt >= note.CreatedAt);
        Assert.True(note.Version > 0);
        var etag = Assert.IsType<string>(response.Headers.ETag?.Tag);
        Assert.False(response.Headers.ETag!.IsWeak);
        Assert.Equal(Tag(note.Version), etag);
        Assert.Equal(TimeSpan.Zero, note.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, note.UpdatedAt.Offset);
        return new NoteResult(note, etag);
    }

    protected static Task<HttpResponseMessage> PutContentAsync(
        HttpClient client,
        Guid id,
        string? etag,
        string title = "Changed title",
        string body = "Changed body")
    {
        return SendAsync(client, HttpMethod.Put, $"/api/notes/{id}",
            JsonSerializer.Serialize(new { title, body }), etag);
    }

    protected static Task<HttpResponseMessage> PutSharingAsync(
        HttpClient client,
        Guid id,
        string? etag,
        params TestGrant[] grants)
    {
        return SendAsync(client, HttpMethod.Put, $"/api/notes/{id}/sharing",
            JsonSerializer.Serialize(new { grants }, JsonOptions), etag);
    }

    protected static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? json = null,
        string? etag = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = Json(json);
        }

        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return await client.SendAsync(request);
    }

    protected async Task<(Guid TeamA, Guid TeamB)> TeamsAsync()
    {
        using var response = await Alice.GetAsync("/api/teams");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.GetProperty("items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        var teams = items.EnumerateArray().ToDictionary(
            item => item.GetProperty("name").GetString()!,
            item => item.GetProperty("id").GetGuid());
        return (teams["Team A"], teams["Team B"]);
    }

    protected static async Task<TestPage> ListAsync(HttpClient client, int limit = 100, int offset = 0)
    {
        using var response = await client.GetAsync($"/api/notes?limit={limit}&offset={offset}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.False(item.TryGetProperty("body", out _));
        }

        var page = json.RootElement.Deserialize<TestPage>(JsonOptions);
        Assert.NotNull(page);
        Assert.All(page.Items, item =>
        {
            Assert.NotEqual(Guid.Empty, item.Id);
            Assert.NotEqual(Guid.Empty, item.OwnerId);
            Assert.False(string.IsNullOrEmpty(item.Title));
            Assert.True(item.Version > 0);
            Assert.Equal(TimeSpan.Zero, item.CreatedAt.Offset);
            Assert.Equal(TimeSpan.Zero, item.UpdatedAt.Offset);
        });
        Assert.True(page.NextOffset is null || page.Items.Count > 0);
        if (page.NextOffset is not null)
        {
            Assert.Equal(offset + page.Items.Count, page.NextOffset.Value);
        }

        return page;
    }

    protected static async Task<List<TestSummary>> AllNotesAsync(HttpClient client)
    {
        List<TestSummary> result = [];
        HashSet<Guid> seen = [];
        int? offset = 0;
        while (offset is not null)
        {
            var page = await ListAsync(client, 100, offset.Value);
            Assert.All(page.Items, item => Assert.True(seen.Add(item.Id), "A paginated note was returned more than once."));
            result.AddRange(page.Items);
            Assert.True(page.NextOffset is null || page.NextOffset > offset);
            offset = page.NextOffset;
        }

        return result;
    }

    protected static async Task<string> ReadSharingTagAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"/api/notes/{id}/sharing");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        Assert.False(response.Headers.ETag?.IsWeak);
        return Assert.IsType<string>(response.Headers.ETag?.Tag);
    }

    protected static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        bool authenticated = true,
        bool noVersion = false)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal((int)expected, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("type").GetString()));
        if (authenticated)
        {
            AssertNoStore(response);
        }

        if (noVersion)
        {
            Assert.Null(response.Headers.ETag);
            Assert.False(root.TryGetProperty("version", out _));
            Assert.False(root.TryGetProperty("grants", out _));
            Assert.False(root.TryGetProperty("ownerId", out _));
        }
    }

    protected static void AssertNoStore(HttpResponseMessage response) =>
        Assert.True(response.Headers.CacheControl?.NoStore,
            $"Expected Cache-Control: no-store on {(int)response.StatusCode}.");

    protected sealed record NoteResult(TestNote Note, string ETag);
    protected sealed record TestNote(
        Guid Id,
        Guid OwnerId,
        string Title,
        string Body,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        long Version);
    protected sealed record TestGrant(string Audience, Guid? TeamId, string Permission);
    protected sealed record TestSummary(
        Guid Id,
        Guid OwnerId,
        string Title,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        long Version);
    protected sealed record TestPage(IReadOnlyList<TestSummary> Items, int? NextOffset);
}
