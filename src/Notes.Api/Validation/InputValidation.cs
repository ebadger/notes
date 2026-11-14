using System.Text;
using Notes.Api.Contracts;

namespace Notes.Api.Validation;

public static class InputValidation
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public const int MaxTitleLength = 200;
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxRequestBytes = 1024 * 1024;

    public static Dictionary<string, string[]> ValidateContent(NoteContentRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Title is null || request.Title.Trim().Length is < 1 or > MaxTitleLength)
        {
            errors["title"] = ["Title must contain 1 through 200 UTF-16 code units after trimming."];
        }
        else if (!IsValidDatabaseText(request.Title, out _))
        {
            errors["title"] = ["Title must be valid Unicode without null characters."];
        }

        if (request.Body is null)
        {
            errors["body"] = ["Body is required and must be a string."];
        }
        else if (!IsValidDatabaseText(request.Body, out var bodyBytes))
        {
            errors["body"] = ["Body must be valid Unicode without null characters."];
        }
        else if (bodyBytes > MaxBodyBytes)
        {
            errors["body"] = ["Body must not exceed 64 KiB of decoded UTF-8 text."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidateSharing(
        SharingRequest request,
        IReadOnlySet<Guid> existingTeamIds)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Grants is null)
        {
            errors["grants"] = ["Grants must be an array; use an empty array to make the note private."];
            return errors;
        }

        var targets = new HashSet<(GrantAudience Audience, Guid? TeamId)>();
        for (var index = 0; index < request.Grants.Count; index++)
        {
            var prefix = $"grants[{index}]";
            var grant = request.Grants[index];
            if (grant is null)
            {
                errors[prefix] = ["A grant must be an object."];
                continue;
            }

            if (grant.Audience is null || !Enum.IsDefined(grant.Audience.Value))
            {
                errors[$"{prefix}.audience"] = ["Audience must be team or organization."];
            }

            if (grant.Permission is null || !Enum.IsDefined(grant.Permission.Value))
            {
                errors[$"{prefix}.permission"] = ["Permission must be read or edit."];
            }

            if (grant.Audience == GrantAudience.Team)
            {
                if (grant.TeamId is null)
                {
                    errors[$"{prefix}.teamId"] = ["A team grant requires a team ID."];
                }
                else if (!existingTeamIds.Contains(grant.TeamId.Value))
                {
                    errors[$"{prefix}.teamId"] = ["The team does not exist."];
                }
            }
            else if (grant.Audience == GrantAudience.Organization && grant.TeamId is not null)
            {
                errors[$"{prefix}.teamId"] = ["An organization grant must not have a team ID."];
            }

            if (grant.Audience is { } audience &&
                !targets.Add((audience, audience == GrantAudience.Organization ? null : grant.TeamId)))
            {
                errors[$"{prefix}.audience"] = ["Each team or organization target may appear only once."];
            }
        }

        return errors;
    }

    private static bool IsValidDatabaseText(string value, out int utf8Bytes)
    {
        utf8Bytes = 0;
        if (value.Contains('\0'))
        {
            return false;
        }

        try
        {
            utf8Bytes = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
