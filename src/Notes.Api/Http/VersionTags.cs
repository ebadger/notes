using System.Globalization;

namespace Notes.Api.Http;

public static class VersionTags
{
    public static bool TryParse(string? value, out long version)
    {
        version = 0;
        if (value is null || value.Length is < 3 or > 21 ||
            value[0] != '"' || value[^1] != '"' || value[1] is < '1' or > '9')
        {
            return false;
        }

        var digits = value.AsSpan(1, value.Length - 2);
        foreach (var character in digits)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    public static string Format(long version)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        return $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";
    }
}
