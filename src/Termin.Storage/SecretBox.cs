using System.Security.Cryptography;
using System.Text;

namespace Termin.Storage;

/// <summary>
/// AES-256-GCM encryption for backend secrets at rest. The key comes from
/// TERMIN_SECRET_KEY and is never stored in the database.
/// Layout: version (1) | nonce (12) | tag (16) | ciphertext.
/// </summary>
public sealed class SecretBox
{
    private const byte Version = 1;
    private readonly byte[] _key;

    public SecretBox(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("The secret key must be 32 bytes.", nameof(key));
        _key = key;
    }

    /// <summary>
    /// Accepts a base64 encoded 32-byte key. Any other string is stretched with
    /// SHA-256, which is fine for long random passphrases only.
    /// </summary>
    public static SecretBox FromConfig(string keyMaterial, out bool derived)
    {
        derived = false;
        try
        {
            var raw = Convert.FromBase64String(keyMaterial.Trim());
            if (raw.Length == 32) return new SecretBox(raw);
        }
        catch (FormatException)
        {
        }
        derived = true;
        return new SecretBox(SHA256.HashData(Encoding.UTF8.GetBytes("termin-secret-key:" + keyMaterial)));
    }

    public byte[] Encrypt(string plaintext, string associatedData)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var gcm = new AesGcm(_key, 16))
            gcm.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(associatedData));

        var output = new byte[1 + nonce.Length + tag.Length + cipher.Length];
        output[0] = Version;
        nonce.CopyTo(output, 1);
        tag.CopyTo(output, 13);
        cipher.CopyTo(output, 29);
        return output;
    }

    public string Decrypt(byte[] blob, string associatedData)
    {
        if (blob.Length < 29 || blob[0] != Version) throw new CryptographicException("Unknown secret format.");
        var nonce = blob.AsSpan(1, 12);
        var tag = blob.AsSpan(13, 16);
        var cipher = blob.AsSpan(29);
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(_key, 16);
        gcm.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(associatedData));
        return Encoding.UTF8.GetString(plain);
    }
}
