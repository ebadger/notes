using Notes.Api.Http;

namespace Notes.Api.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class VersionTagsTests
{
    [Theory]
    [InlineData("\"1\"", 1L)]
    [InlineData("\"10\"", 10L)]
    [InlineData("\"2147483648\"", 2147483648L)]
    [InlineData("\"9223372036854775807\"", long.MaxValue)]
    public void Canonical_strong_versions_round_trip(string value, long expected)
    {
        Assert.True(VersionTags.TryParse(value, out var actual));
        Assert.Equal(expected, actual);
        Assert.Equal(value, VersionTags.Format(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("\"0\"")]
    [InlineData("\"01\"")]
    [InlineData("\"001\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"+1\"")]
    [InlineData("\"1.0\"")]
    [InlineData("\"1e0\"")]
    [InlineData("\" 1\"")]
    [InlineData("\"1 \"")]
    [InlineData(" \"1\"")]
    [InlineData("\"1\" ")]
    [InlineData("\"\"")]
    [InlineData("'1'")]
    [InlineData("W/\"1\"")]
    [InlineData("w/\"1\"")]
    [InlineData("*")]
    [InlineData("\"*\"")]
    [InlineData("\"1\", \"2\"")]
    [InlineData("\"1\",\"1\"")]
    [InlineData("\"9223372036854775808\"")]
    [InlineData("\"999999999999999999999999999999999\"")]
    [InlineData("\"\u0661\"")]
    [InlineData("\"\uff11\"")]
    [InlineData("\"1\"\r\n")]
    [InlineData("\"1")]
    [InlineData("1\"")]
    public void Unsupported_preconditions_are_rejected(string? value)
    {
        Assert.False(VersionTags.TryParse(value, out _));
    }
}
