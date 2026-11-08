using System.Runtime.Versioning;
using System.ServiceProcess;

namespace NetGuard.Windows;

/// <summary>Start/stop/enable/disable the NetGuard service (administrators only – Windows enforces that).</summary>
[SupportedOSPlatform("windows")]
public static class ServiceControl
{
    public const string ServiceName = "NetGuard";

    public static bool IsInstalled() => ServiceController.GetServices().Any(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));

    public static ServiceControllerStatus? Status()
    {
        if (!IsInstalled()) return null;
        using var sc = new ServiceController(ServiceName);
        return sc.Status;
    }

    public static bool IsRunning() => Status() == ServiceControllerStatus.Running;

    public static string Describe() => Status() switch
    {
        null => "not installed",
        ServiceControllerStatus.Running => "running",
        var s => s.ToString()!.ToLowerInvariant(),
    } + (IsInstalled() && IsDisabled() ? " (disabled)" : "");

    public static bool IsDisabled()
    {
        using var sc = new ServiceController(ServiceName);
        return sc.StartType == ServiceStartMode.Disabled;
    }

    /// <summary>Stop the service and keep it from starting again (used by emergency recovery).</summary>
    public static void StopAndDisable()
    {
        if (!IsInstalled()) return;
        Sc($"config {ServiceName} start= disabled");
        using var sc = new ServiceController(ServiceName);
        if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    /// <summary>Turn protection back on: automatic start, and start it now.</summary>
    public static void EnableAndStart()
    {
        if (!IsInstalled()) throw new InvalidOperationException("The NetGuard service isn't installed. Run NetGuardSetup.exe.");
        Sc($"config {ServiceName} start= auto");
        using var sc = new ServiceController(ServiceName);
        if (sc.Status != ServiceControllerStatus.Running) sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    /// <summary>Make sure a Windows service is set to start automatically and is running. Returns what had to be fixed, or null.</summary>
    public static string? EnsureRunning(string name)
    {
        string? fixedWhat = null;
        using var sc = new ServiceController(name);
        if (sc.StartType == ServiceStartMode.Disabled)
        {
            Sc($"config {name} start= auto");
            fixedWhat = $"{name} was disabled";
        }
        sc.Refresh();
        if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending or ServiceControllerStatus.Paused)
        {
            if (sc.Status == ServiceControllerStatus.Paused) sc.Continue(); else sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            fixedWhat = (fixedWhat == null ? "" : fixedWhat + ", ") + $"{name} was not running";
        }
        return fixedWhat;
    }

    private static void Sc(string args)
    {
        var r = ProcessRunner.Run("sc.exe", args);
        if (r.ExitCode != 0) throw new InvalidOperationException($"sc {args}: {r.Output}");
    }
}
