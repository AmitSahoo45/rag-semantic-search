using System.Security.Cryptography;
using System.Text;

namespace SemanticRagSearch.Api.Authentication;

public static class ApiKeyCredentials
{
    public const string HeaderName = "X-API-Key";
    public const string Scheme = "ApiKey";
    public const string TenantClaim = "tenant_id";

    public static string Generate() => "srk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
        .ToLowerInvariant();

    public static bool IsValidFormat(string key) => key.Length is >= 32 and <= 128 &&
        key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
