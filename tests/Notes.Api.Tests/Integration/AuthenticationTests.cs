using System.Net;
using Notes.Api.Tests.Infrastructure;

namespace Notes.Api.Tests.Integration;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AuthenticationTests(PostgreSqlFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Health_is_available_without_credentials()
    {
        using var response = await Fixture.Anonymous.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/teams")]
    [InlineData("GET", "/api/notes")]
    [InlineData("POST", "/api/notes")]
    [InlineData("GET", "/api/notes/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "/api/notes/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/notes/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/notes/00000000-0000-0000-0000-000000000001/sharing")]
    [InlineData("PUT", "/api/notes/00000000-0000-0000-0000-000000000001/sharing")]
    public async Task Every_api_route_requires_authentication(string method, string route)
    {
        using var response = await SendAsync(Fixture.Anonymous, new HttpMethod(method), route, "{}");
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, authenticated: false, noVersion: true);
        Assert.Contains(response.Headers.WwwAuthenticate,
            challenge => challenge.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Bearer not-a-valid-key")]
    [InlineData("Bearer 11111111-1111-1111-1111-111111111111")]
    [InlineData("Basic YWxpY2U6cGFzc3dvcmQ=")]
    [InlineData("Bearer")]
    [InlineData("Bearer first second")]
    public async Task Invalid_credentials_are_not_treated_as_identity(string authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/notes");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var response = await Fixture.Anonymous.SendAsync(request);
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, authenticated: false, noVersion: true);
    }

    [Fact]
    public async Task Identity_headers_neither_authenticate_nor_override_an_authenticated_user()
    {
        var created = await CreateNoteAsync();
        foreach (var client in new[] { Fixture.Anonymous, Bob })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/notes/{created.Note.Id}");
            foreach (var header in new[] { "X-User-Id", "X-UserId", "X-Authenticated-User", "X-Api-Key" })
            {
                request.Headers.TryAddWithoutValidation(header, created.Note.OwnerId.ToString());
            }

            using var response = await client.SendAsync(request);
            await AssertProblemAsync(response,
                client == Fixture.Anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound,
                authenticated: client != Fixture.Anonymous,
                noVersion: true);
        }
    }

    [Fact]
    public async Task Query_string_keys_do_not_authenticate()
    {
        using var response = await Fixture.Anonymous.GetAsync(
            $"/api/notes?api_key={Fixture.ApiKey(TestUser.Alice)}");
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, authenticated: false, noVersion: true);
    }

    [Fact]
    public async Task All_seeded_identities_can_discover_the_same_existing_teams()
    {
        var teams = await TeamsAsync();
        Assert.NotEqual(Guid.Empty, teams.TeamA);
        Assert.NotEqual(Guid.Empty, teams.TeamB);
        Assert.NotEqual(teams.TeamA, teams.TeamB);
        foreach (var user in Enum.GetValues<TestUser>())
        {
            using var response = await Fixture.Client(user).GetAsync("/api/teams");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertNoStore(response);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Contains(teams.TeamA.ToString(), text);
            Assert.Contains(teams.TeamB.ToString(), text);
            Assert.DoesNotContain(Fixture.ApiKey(user), text);
        }
    }
}
