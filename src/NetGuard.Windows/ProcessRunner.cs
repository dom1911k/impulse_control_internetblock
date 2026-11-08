using System.Diagnostics;

namespace NetGuard.Windows;

public sealed record ProcessResult(int ExitCode, string Output);

public static class ProcessRunner
{
    public static ProcessResult Run(string exe, string args, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"{exe} {args} timed out");
        }
        return new ProcessResult(p.ExitCode, (stdout.Result + stderr.Result).Trim());
    }
}
