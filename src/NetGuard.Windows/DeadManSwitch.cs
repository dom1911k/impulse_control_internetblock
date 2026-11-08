using System.Runtime.Versioning;
using System.Text;
using System.Xml.Linq;

namespace NetGuard.Windows;

/// <summary>
/// Test-mode safety net that does not depend on ngctl staying alive: a one-shot Windows scheduled task
/// (running as SYSTEM) that restores normal internet at the deadline – and also at the next boot, in case
/// the PC is restarted before the deadline. It first runs the plain PowerShell recovery commands, then
/// "ngctl recover" for a tidy cleanup (re-enable rules, hosts file, state, log).
/// </summary>
[SupportedOSPlatform("windows")]
public static class DeadManSwitch
{
    public const string TaskName = @"\NetGuard\DeadManSwitch";

    public const string PowerShellRecovery =
        "Get-NetFirewallRule -Group 'NetGuard' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; " +
        "Set-NetFirewallProfile -All -DefaultOutboundAction Allow";

    public static void Arm(DateTime deadlineLocal, string ngctlPath, string workDir)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task", new XAttribute("version", "1.2"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Description", "NetGuard test mode: restores normal internet automatically. Safe to delete after the test.")),
                new XElement(ns + "Triggers",
                    new XElement(ns + "TimeTrigger",
                        new XElement(ns + "StartBoundary", deadlineLocal.ToString("yyyy-MM-dd'T'HH:mm:ss")),
                        new XElement(ns + "Enabled", "true")),
                    new XElement(ns + "BootTrigger", new XElement(ns + "Enabled", "true"))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", "S-1-5-18"),
                        new XElement(ns + "RunLevel", "HighestAvailable"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "WakeToRun", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT10M"),
                    new XElement(ns + "Priority", "4")),
                new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", "powershell.exe"),
                        new XElement(ns + "Arguments", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{PowerShellRecovery}\"")),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", ngctlPath),
                        new XElement(ns + "Arguments", "recover --reason dead-man-switch --from-task"),
                        new XElement(ns + "WorkingDirectory", workDir)))));

        var xmlPath = Path.Combine(Path.GetTempPath(), "netguard-deadman.xml");
        using (var w = new StreamWriter(xmlPath, false, Encoding.Unicode)) doc.Save(w);
        try
        {
            var r = ProcessRunner.Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (r.ExitCode != 0) throw new InvalidOperationException("Could not create the dead-man-switch task: " + r.Output);
        }
        finally { try { File.Delete(xmlPath); } catch { } }
    }

    public static void Disarm()
    {
        if (IsArmed()) ProcessRunner.Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
    }

    public static bool IsArmed() => ProcessRunner.Run("schtasks.exe", $"/Query /TN \"{TaskName}\"").ExitCode == 0;
}
