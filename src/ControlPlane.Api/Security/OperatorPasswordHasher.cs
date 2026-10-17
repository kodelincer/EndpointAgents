using System.Security.Cryptography;
using System.Text;

namespace ControlPlane.Api.Security;

/// <summary>PBKDF2-SHA256 password hashes stored as "pbkdf2-sha256$iterations$salt$hash".</summary>
public static class OperatorPasswordHasher
{
    public const int MinimumLength = 10;
    public const int MaximumLength = 128;
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Prefix = "pbkdf2-sha256";

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt,
            Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix
            || !int.TryParse(parts[1], out var iterations) || iterations < 10_000)
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt,
                iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Rejects passwords that are too short or too long to be hashed safely.</summary>
    public static string? Validate(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinimumLength
            ? $"Password must have at least {MinimumLength} characters."
            : password.Length > MaximumLength
                ? $"Password must have at most {MaximumLength} characters."
                : null;

    /// <summary>A readable one-time password for new accounts and resets.</summary>
    public static string GenerateTemporary()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = RandomNumberGenerator.GetString(alphabet, 16);
        return $"{chars[..4]}-{chars[4..8]}-{chars[8..12]}-{chars[12..]}";
    }
}
