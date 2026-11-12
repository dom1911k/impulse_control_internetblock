using System.Reflection;
using System.Runtime.Versioning;

namespace NetGuard.Windows;

public sealed record ScheduledTaskInfo(string Path, string UserId, string Actions, bool Enabled);

/// <summary>
/// Lists scheduled tasks through the Task Scheduler API ("Schedule.Service"), so NetGuard can notice the
/// "run a command as SYSTEM via a scheduled task" trick during a lock, and switch such a task off.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TaskWatcher
{
    private static readonly string[] ShellTools =
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "sc.exe", "net.exe",
        "net1.exe", "netsh.exe", "taskkill.exe", "reg.exe", "schtasks.exe", "bash.exe", "wsl.exe",
    };

    public static List<ScheduledTaskInfo> List()
    {
        var list = new List<ScheduledTaskInfo>();
        var svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        Call(svc, "Connect");
        Walk(Call(svc, "GetFolder", "\\")!, list, 0);
        return list;
    }

    private static void Walk(object folder, List<ScheduledTaskInfo> list, int depth)
    {
        var tasks = Call(folder, "GetTasks", 1)!; // include hidden
        var n = Convert.ToInt32(Get(tasks, "Count"));
        for (var i = 1; i <= n; i++)
        {
            try
            {
                var t = Get(tasks, "Item", i)!;
                var def = Get(t, "Definition")!;
                var principal = Get(def, "Principal")!;
                var user = Get(principal, "UserId") as string ?? "";
                var actions = Get(def, "Actions")!;
                var parts = new List<string>();
                var an = Convert.ToInt32(Get(actions, "Count"));
                for (var a = 1; a <= an; a++)
                {
                    var action = Get(actions, "Item", a)!;
                    try { parts.Add(((Get(action, "Path") as string) ?? "") + " " + ((Get(action, "Arguments") as string) ?? "")); } catch { }
                }
                list.Add(new ScheduledTaskInfo(Get(t, "Path") as string ?? "", user, string.Join(" | ", parts), Convert.ToBoolean(Get(t, "Enabled"))));
            }
            catch { }
        }
        if (depth > 6) return;
        var subs = Call(folder, "GetFolders", 0)!;
        var sn = Convert.ToInt32(Get(subs, "Count"));
        for (var i = 1; i <= sn; i++)
        {
            try { Walk(Get(subs, "Item", i)!, list, depth + 1); } catch { }
        }
    }

    public static bool RunsAsSystem(ScheduledTaskInfo t) =>
        t.UserId.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) || t.UserId == "S-1-5-18" ||
        t.UserId.EndsWith("\\SYSTEM", StringComparison.OrdinalIgnoreCase);

    /// <summary>Does the task start a shell or script tool (the usual way to misuse it)?</summary>
    public static bool RunsShellTool(ScheduledTaskInfo t) =>
        ShellTools.Any(tool => t.Actions.Contains(tool, StringComparison.OrdinalIgnoreCase) ||
                               t.Actions.Split(' ', '|').Any(p => p.Trim('"').Equals(Path.GetFileNameWithoutExtension(tool), StringComparison.OrdinalIgnoreCase)));

    public static bool IsExempt(ScheduledTaskInfo t) =>
        t.Path.StartsWith("\\Microsoft\\", StringComparison.OrdinalIgnoreCase) || t.Path.StartsWith("\\NetGuard\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Switch the task off and stop it if it's running.</summary>
    public static void Disable(string path)
    {
        ProcessRunner.Run("schtasks.exe", $"/End /TN \"{path}\"");
        ProcessRunner.Run("schtasks.exe", $"/Change /TN \"{path}\" /DISABLE");
    }

    private static object? Get(object o, string name, params object[] args) =>
        o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args);

    private static object? Call(object o, string name, params object[] args) =>
        o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
}
