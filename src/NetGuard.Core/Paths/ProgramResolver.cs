using System.Security.Cryptography;
using NetGuard.Core.Config;

namespace NetGuard.Core.Paths;

public sealed record SignatureInfo(bool IsTrusted, string? Subject, string? Error);

public interface ISignatureVerifier
{
    SignatureInfo Check(string path);
}

/// <summary>Used where no signature checking is available (tests). Treats every file as unsigned.</summary>
public sealed class NoSignatureVerifier : ISignatureVerifier
{
    public SignatureInfo Check(string path) => new(false, null, "signature checking not available on this platform");
}

public enum ResolvedStatus { Allowed, Rejected }

public sealed record ResolvedProgram(string EntryName, string Path, ResolvedStatus Status, string Detail);

public sealed class ProgramResolver
{
    private readonly ISignatureVerifier _verifier;
    private readonly Func<string, string> _expandEnv;

    public ProgramResolver(ISignatureVerifier verifier, Func<string, string>? expandEnv = null)
    {
        _verifier = verifier;
        _expandEnv = expandEnv ?? Environment.ExpandEnvironmentVariables;
    }

    /// <summary>Find every file for an entry and decide whether it passes its pin check.</summary>
    public List<ResolvedProgram> Resolve(ProgramEntry entry, bool checkPins = true)
    {
        var list = new List<ResolvedProgram>();
        var files = entry.Paths.SelectMany(p => PathGlob.Expand(p, _expandEnv))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (!checkPins || entry.Pin == PinMode.None)
            {
                list.Add(new(entry.Name, file, ResolvedStatus.Allowed, checkPins ? "no pin" : "block rule"));
                continue;
            }

            if (entry.Pin == PinMode.Sha256)
            {
                string actual;
                try { actual = Sha256Of(file); }
                catch (Exception e) { list.Add(new(entry.Name, file, ResolvedStatus.Rejected, "cannot hash: " + e.Message)); continue; }
                list.Add(string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase)
                    ? new(entry.Name, file, ResolvedStatus.Allowed, "SHA-256 matches")
                    : new(entry.Name, file, ResolvedStatus.Rejected, $"SHA-256 mismatch ({actual[..12]}…)"));
                continue;
            }

            var sig = _verifier.Check(file);
            if (!sig.IsTrusted)
                list.Add(new(entry.Name, file, ResolvedStatus.Rejected, "not validly signed" + (sig.Error is { } e ? $" ({e})" : "")));
            else if (sig.Subject == null || !sig.Subject.Contains(entry.Signer!, StringComparison.OrdinalIgnoreCase))
                list.Add(new(entry.Name, file, ResolvedStatus.Rejected, $"signed by someone else: {sig.Subject}"));
            else
                list.Add(new(entry.Name, file, ResolvedStatus.Allowed, $"signed by {entry.Signer}"));
        }
        return list;
    }

    public static string Sha256Of(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }
}
