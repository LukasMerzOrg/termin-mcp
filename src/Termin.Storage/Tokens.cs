using System.Security.Cryptography;
using System.Text;

namespace Termin.Storage;

public static class Tokens
{
    /// <summary>A random opaque token, base64url, 256 bits.</summary>
    public static string New(string prefix = "") => prefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Tokens are stored as SHA-256 hashes only.</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>PKCE S256: BASE64URL(SHA256(verifier)) == challenge.</summary>
    public static bool VerifyPkce(string verifier, string challenge)
    {
        if (verifier.Length is < 43 or > 128) return false;
        var computed = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    public static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
