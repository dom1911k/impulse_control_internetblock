using System.Runtime.Versioning;
using NetGuard.Core;
using NetGuard.Core.Config;
using NetGuard.Core.Logging;
using NetGuard.Core.State;

namespace NetGuard.Windows;

/// <summary>
/// Locks with a dead-man switch: the caller restores internet at the deadline, and independently a
/// SYSTEM scheduled task does so 30 s later (or at boot) in case the caller crashed or was closed.
/// Shared by ngctl and the NetGuard window app.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TestMode
{
    public const int MaxMinutes = 15;

    /// <summary>Full path of ngctl.exe next to the running program (the dead-man task runs it).</summary>
    public static string NgctlPath => Path.Combine(Updater.AppDir, "ngctl.exe");

    public static (TestModeInfo info, ExecutionResult result) Start(Engine engine, NetGuardConfig cfg, string profile, int minutes, string actor)
    {
        if (minutes < 1 || minutes > MaxMinutes) throw new ArgumentOutOfRangeException(nameof(minutes), $"Test mode lasts 1–{MaxMinutes} minutes.");
        if (!File.Exists(NgctlPath))
            throw new InvalidOperationException($"ngctl.exe must be in the same folder ({Updater.AppDir}) – the safety task needs it.");
        var profileName = cfg.FindProfile(profile)?.Name ?? throw new ArgumentException($"Unknown profile '{profile}'.");

        var deadline = DateTimeOffset.UtcNow.AddMinutes(minutes);
        // Arm the independent safety net BEFORE locking anything.
        DeadManSwitch.Arm(deadline.ToLocalTime().DateTime.AddSeconds(30), NgctlPath, Updater.AppDir);

        var info = new TestModeInfo { DeadlineUtc = deadline, TaskName = DeadManSwitch.TaskName };
        engine.Log.Append(LogTypes.TestModeStarted, new { profile = profileName, minutes, deadlineUtc = deadline }, actor);
        try
        {
            var (_, _, result) = engine.ApplyProfile(cfg, profileName, actor, info);
            return (info, result);
        }
        catch
        {
            engine.Recover("apply failed in test mode", actor);
            DeadManSwitch.Disarm();
            throw;
        }
    }

    public static ExecutionResult End(Engine engine, string actor, bool endedEarly)
    {
        var profile = engine.LoadState().ActiveProfile;
        var (_, result) = engine.Recover(endedEarly ? "test mode ended early by user" : "test mode deadline reached", actor);
        try { DeadManSwitch.Disarm(); } catch { /* best effort; the task also cleans up after itself */ }
        engine.Log.Append(LogTypes.TestModeEnded, new { profile, endedEarly }, actor);
        return result;
    }
}
