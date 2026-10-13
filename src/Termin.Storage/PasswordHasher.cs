using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Termin.Storage;

/// <summary>Argon2id password hashing in the PHC string format.</summary>
public static class PasswordHasher
{
    private const int MemoryKiB = 65536;
    private const int Iterations = 3;
    private const int Parallelism = 1;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Compute(password, salt, MemoryKiB, Iterations, Parallelism, 32);
        return $"$argon2id$v=19$m={MemoryKiB},t={Iterations},p={Parallelism}${B64(salt)}${B64(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            // ["", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash]
            if (parts.Length != 6 || parts[1] != "argon2id") return false;
            var p = parts[3].Split(',').Select(x => x.Split('=')).ToDictionary(x => x[0], x => int.Parse(x[1]));
            var salt = FromB64(parts[4]);
            var expected = FromB64(parts[5]);
            var actual = Compute(password, salt, p["m"], p["t"], p["p"], expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Compute(string password, byte[] salt, int memory, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memory,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=');

    private static byte[] FromB64(string s) => Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
}
