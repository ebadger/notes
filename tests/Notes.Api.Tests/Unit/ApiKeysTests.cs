using Notes.Api.Authentication;

namespace Notes.Api.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class ApiKeysTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void Missing_or_short_keys_are_invalid(string? value)
    {
        Assert.False(ApiKeys.HasValidFormat(value));
    }

    [Theory]
    [InlineData(31, false)]
    [InlineData(32, true)]
    [InlineData(33, true)]
    [InlineData(1024, true)]
    public void Minimum_key_length_is_exact(int length, bool expected)
    {
        Assert.Equal(expected, ApiKeys.HasValidFormat(new string('a', length)));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\u2003")]
    [InlineData("\u00a0")]
    public void Whitespace_anywhere_in_a_key_is_invalid(string whitespace)
    {
        var valid = new string('a', 32);
        Assert.False(ApiKeys.HasValidFormat(whitespace + valid));
        Assert.False(ApiKeys.HasValidFormat(valid + whitespace));
        Assert.False(ApiKeys.HasValidFormat(valid[..16] + whitespace + valid[16..]));
    }

    [Fact]
    public void Hash_uses_known_sha256_vector_and_uppercase_hex()
    {
        Assert.Equal(
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
            ApiKeys.Hash("abc"));
        Assert.NotEqual(ApiKeys.Hash(new string('a', 32)), ApiKeys.Hash(new string('A', 32)));
        Assert.Equal(ApiKeys.Hash(new string('a', 32)), ApiKeys.Hash(new string('a', 32)));
    }
}
