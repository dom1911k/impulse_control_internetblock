using System.Security.Cryptography;
using System.Text;
using NetGuard.Core.State;

namespace NetGuard.Core.Security;

/// <summary>
/// The emergency key. Shown exactly once when created; only a salted PBKDF2 hash is stored.
/// 30 characters from a 32-letter alphabet ≈ 150 bits – guessing is hopeless, and wrong
/// attempts are rate-limited and logged anyway.
/// </summary>
public static class RecoveryCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I
    private const int Groups = 6, GroupLength = 5, Iterations = 200_000;
    public const int MaxWrongAttemptsPerHour = 5;

    /// <summary>e.g. "K7M2Q-…-ZP4XH"</summary>
    public static string Generate()
    {
        var sb = new StringBuilder();
        for (var g = 0; g < Groups; g++)
        {
            if (g > 0) sb.Append('-');
            for (var i = 0; i < GroupLength; i++) sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }
        return sb.ToString();
    }

    /// <summary>Case, spaces and dashes don't matter.</summary>
    public static string Normalize(string input) =>
        new(input.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static void Store(NetGuardState state, string code)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        state.RecoveryCodeSalt = Convert.ToBase64String(salt);
        state.RecoveryCodeHash = Convert.ToBase64String(Hash(code, salt));
        state.RecoveryCodeCreatedUtc = DateTimeOffset.UtcNow;
        state.WrongCodeAttemptsUtc.Clear();
    }

    public static bool Exists(NetGuardState state) => !string.IsNullOrEmpty(state.RecoveryCodeHash);

    public enum CheckResult { Ok, Wrong, NoCode, TooManyAttempts }

    /// <summary>Checks a code and records wrong attempts in <paramref name="state"/> (caller saves it).</summary>
    public static CheckResult Check(NetGuardState state, string? code, DateTimeOffset now)
    {
        if (!Exists(state)) return CheckResult.NoCode;
        state.WrongCodeAttemptsUtc.RemoveAll(t => now - t > TimeSpan.FromHours(1));
        if (state.WrongCodeAttemptsUtc.Count >= MaxWrongAttemptsPerHour) return CheckResult.TooManyAttempts;

        var expected = Convert.FromBase64String(state.RecoveryCodeHash!);
        var actual = Hash(code ?? "", Convert.FromBase64String(state.RecoveryCodeSalt!));
        if (CryptographicOperations.FixedTimeEquals(expected, actual)) return CheckResult.Ok;
        state.WrongCodeAttemptsUtc.Add(now);
        return CheckResult.Wrong;
    }

    private static byte[] Hash(string code, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Normalize(code)), salt, Iterations, HashAlgorithmName.SHA256, 32);
}
