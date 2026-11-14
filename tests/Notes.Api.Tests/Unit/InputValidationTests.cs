using System.Text;
using Notes.Api.Contracts;
using Notes.Api.Validation;

namespace Notes.Api.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class InputValidationTests
{
    private static readonly Guid TeamA = Guid.Parse("8bc49cd6-f723-48d9-9d3c-eb9d23a019f1");
    private static readonly Guid TeamB = Guid.Parse("b529c5b2-6807-49a4-b485-330b2f5a931b");
    private static readonly IReadOnlySet<Guid> Teams = new HashSet<Guid> { TeamA, TeamB };

    [Fact]
    public void Limits_match_the_specification_exactly()
    {
        Assert.Equal(200, InputValidation.MaxTitleLength);
        Assert.Equal(64 * 1024, InputValidation.MaxBodyBytes);
        Assert.Equal(1024 * 1024, InputValidation.MaxRequestBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\r\n\t")]
    [InlineData("\u2003\u00a0")]
    public void Missing_or_trimmed_empty_titles_are_invalid(string? title)
    {
        Assert.Contains("title", InputValidation.ValidateContent(new(title, "")).Keys);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(199, true)]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void Title_limit_applies_after_trimming(int length, bool valid)
    {
        var title = $" \r\n{new string('x', length)}\t ";
        var errors = InputValidation.ValidateContent(new(title, ""));
        Assert.Equal(!valid, errors.ContainsKey("title"));
    }

    [Theory]
    [InlineData(99, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void Title_limit_counts_utf16_code_units_not_code_points(int symbols, bool valid)
    {
        var title = string.Concat(Enumerable.Repeat("\U0001f642", symbols));
        Assert.Equal(symbols * 2, title.Length);
        Assert.Equal(!valid, InputValidation.ValidateContent(new(title, "")).ContainsKey("title"));
    }

    [Fact]
    public void Null_body_is_invalid_but_empty_and_whitespace_bodies_are_valid()
    {
        Assert.Contains("body", InputValidation.ValidateContent(new("Title", null)).Keys);
        Assert.Empty(InputValidation.ValidateContent(new("Title", "")));
        Assert.Empty(InputValidation.ValidateContent(new("Title", " \t\r\n")));
    }

    [Theory]
    [InlineData(65535, true)]
    [InlineData(65536, true)]
    [InlineData(65537, false)]
    public void Ascii_body_boundary_is_exact(int bytes, bool valid)
    {
        var body = new string('x', bytes);
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(body));
        Assert.Equal(!valid, InputValidation.ValidateContent(new("Title", body)).ContainsKey("body"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Multibyte_body_boundary_is_decoded_utf8_not_utf16(int suffixBytes, bool valid)
    {
        var body = string.Concat(Enumerable.Repeat("\U0001f642", 16384)) + new string('x', suffixBytes);
        Assert.Equal(65536 + suffixBytes, Encoding.UTF8.GetByteCount(body));
        Assert.Equal(!valid, InputValidation.ValidateContent(new("Title", body)).ContainsKey("body"));
    }

    [Fact]
    public void Content_reports_title_and_body_errors_together()
    {
        var errors = InputValidation.ValidateContent(new(null, null));
        Assert.Contains("title", errors.Keys);
        Assert.Contains("body", errors.Keys);
        Assert.All(errors.Values, messages => Assert.NotEmpty(messages));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void Text_that_PostgreSQL_cannot_store_is_rejected_before_persistence(int codeUnit)
    {
        var text = $"a{(char)codeUnit}b";
        Assert.Contains("title", InputValidation.ValidateContent(new(text, "")).Keys);
        Assert.Contains("body", InputValidation.ValidateContent(new("Title", text)).Keys);
    }

    [Fact]
    public void Empty_sharing_is_valid_and_multiple_distinct_targets_are_valid()
    {
        Assert.Empty(InputValidation.ValidateSharing(new([]), Teams));
        GrantRequest?[] grants =
        [
            new(GrantAudience.Team, TeamA, GrantPermission.Edit),
            new(GrantAudience.Team, TeamB, GrantPermission.Read),
            new(GrantAudience.Organization, null, GrantPermission.Read)
        ];
        Assert.Empty(InputValidation.ValidateSharing(new(grants), Teams));
    }

    [Fact]
    public void Null_collection_and_null_elements_are_rejected()
    {
        Assert.NotEmpty(InputValidation.ValidateSharing(new(null), Teams));
        Assert.NotEmpty(InputValidation.ValidateSharing(new([null]), Teams));
    }

    [Theory]
    [InlineData(null, GrantPermission.Read)]
    [InlineData(GrantAudience.Team, null)]
    [InlineData((GrantAudience)99, GrantPermission.Read)]
    [InlineData(GrantAudience.Team, (GrantPermission)99)]
    public void Required_and_defined_enum_values_are_enforced(
        GrantAudience? audience,
        GrantPermission? permission)
    {
        Assert.NotEmpty(InputValidation.ValidateSharing(
            new([new(audience, TeamA, permission)]), Teams));
    }

    [Fact]
    public void Team_grants_require_an_existing_nonempty_team_id()
    {
        Guid?[] invalid = [null, Guid.Empty, Guid.NewGuid()];
        foreach (var teamId in invalid)
        {
            Assert.NotEmpty(InputValidation.ValidateSharing(
                new([new(GrantAudience.Team, teamId, GrantPermission.Read)]), Teams));
        }
    }

    [Fact]
    public void Organization_grants_forbid_team_ids()
    {
        Assert.NotEmpty(InputValidation.ValidateSharing(
            new([new(GrantAudience.Organization, TeamA, GrantPermission.Read)]), Teams));
    }

    [Theory]
    [InlineData(GrantAudience.Team)]
    [InlineData(GrantAudience.Organization)]
    public void Duplicate_targets_are_invalid_even_when_permissions_differ(GrantAudience audience)
    {
        Guid? target = audience == GrantAudience.Team ? TeamA : null;
        Assert.NotEmpty(InputValidation.ValidateSharing(new(
        [
            new(audience, target, GrantPermission.Read),
            new(audience, target, GrantPermission.Edit)
        ]), Teams));
    }
}
