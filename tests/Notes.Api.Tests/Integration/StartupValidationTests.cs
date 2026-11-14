using Notes.Api.Tests.Infrastructure;

namespace Notes.Api.Tests.Integration;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StartupValidationTests(PostgreSqlFixture fixture) : ApiTestBase(fixture)
{
    [Theory]
    [InlineData("missing")]
    [InlineData("short")]
    [InlineData("whitespace")]
    [InlineData("duplicate")]
    public async Task Invalid_demo_key_configuration_fails_startup_without_resetting_notes(string scenario)
    {
        var note = await CreateNoteAsync();
        var keys = Enum.GetValues<TestUser>().ToDictionary(user => user, Fixture.ApiKey);
        keys[TestUser.Alice] = scenario switch
        {
            "missing" => "",
            "short" => new string('a', 31),
            "whitespace" => new string('a', 32) + " ",
            "duplicate" => keys[TestUser.Bob],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        await using var factory = new NotesApplicationFactory(Fixture.ConnectionString, keys);
        factory.UseKestrel(0);
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("key", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        foreach (var key in Enum.GetValues<TestUser>().Select(Fixture.ApiKey))
        {
            Assert.DoesNotContain(key, exception.ToString());
        }

        Assert.Equal(note, await GetNoteAsync(Alice, note.Note.Id));
    }
}
