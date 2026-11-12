using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NetGuard.Windows;

/// <summary>
/// Runs Windows Update (through Windows' own update API, "Microsoft.Update.Session") and the Defender
/// definition update, for "Update &amp; shut down". Runs as SYSTEM inside the service.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsUpdater
{
    public sealed record Result(int Found, int Installed, bool RebootRequired, string Summary);

    /// <summary>Search, download and install all pending software updates. Reports progress text.</summary>
    public static Result RunWindowsUpdate(Action<string> progress, CancellationToken ct)
    {
        var session = Create("Microsoft.Update.Session");
        try { Set(session, "ClientApplicationID", "NetGuard"); } catch { }

        progress("Searching for Windows updates…");
        var searcher = Call(session, "CreateUpdateSearcher")!;
        var result = Call(searcher, "Search", "IsInstalled=0 and IsHidden=0 and Type='Software'")!;
        var updates = Get(result, "Updates")!;
        var count = Convert.ToInt32(Get(updates, "Count"));
        if (count == 0) return new Result(0, 0, false, "Windows is up to date.");
        ct.ThrowIfCancellationRequested();

        var coll = Create("Microsoft.Update.UpdateColl");
        var titles = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var u = Get(updates, "Item", i)!;
            try { if (!Convert.ToBoolean(Get(u, "EulaAccepted"))) Call(u, "AcceptEula"); } catch { }
            Call(coll, "Add", u);
            titles.Add(Get(u, "Title") as string ?? "update");
        }

        progress($"Downloading {count} update(s)…");
        var downloader = Call(session, "CreateUpdateDownloader")!;
        Set(downloader, "Updates", coll);
        Call(downloader, "Download");
        ct.ThrowIfCancellationRequested();

        progress($"Installing {count} update(s)… this can take a while.");
        var installer = Call(session, "CreateUpdateInstaller")!;
        Set(installer, "Updates", coll);
        var installResult = Call(installer, "Install")!;
        var reboot = Convert.ToBoolean(Get(installResult, "RebootRequired"));
        var installed = 0;
        for (var i = 0; i < count; i++)
        {
            try
            {
                var r = Call(installResult, "GetUpdateResult", i)!;
                if (Convert.ToInt32(Get(r, "ResultCode")) is 2 or 3) installed++; // succeeded / succeeded with errors
            }
            catch { }
        }
        return new Result(count, installed, reboot,
            $"{installed} of {count} Windows update(s) installed" + (reboot ? " – the rest finishes when the PC shuts down." : ".") +
            "\n" + string.Join("\n", titles.Take(10).Select(t => "• " + t)));
    }

    /// <summary>Refresh Microsoft Defender's definitions. Returns a short result text.</summary>
    public static string UpdateDefender(TimeSpan timeout)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        if (!File.Exists(exe)) return "Defender not found – skipped.";
        var r = ProcessRunner.Run(exe, "-SignatureUpdate", (int)timeout.TotalMilliseconds);
        return r.ExitCode == 0 ? "Defender definitions updated." : $"Defender update didn't work (code {r.ExitCode}).";
    }

    /// <summary>Shut down in <paramref name="seconds"/> seconds (cancel with "shutdown /a"); pending updates finish on the way.</summary>
    public static void ShutDown(int seconds) =>
        ProcessRunner.Run("shutdown.exe", $"/s /t {seconds} /c \"NetGuard: updates are done – the PC shuts down in {seconds} seconds. To cancel: Win+R, shutdown /a\"");

    private static object Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;

    private static object? Get(object o, string name, params object[] args) =>
        o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args);

    private static void Set(object o, string name, object value) =>
        o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { value });

    private static object? Call(object o, string name, params object[] args) =>
        o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
}
