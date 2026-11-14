using System.Security.Cryptography;
using System.Text;

namespace Notes.Api.Authentication;

public static class ApiKeys
{
    public static bool HasValidFormat(string? value) =>
        value is { Length: >= 32 } && !value.Any(char.IsWhiteSpace);

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
