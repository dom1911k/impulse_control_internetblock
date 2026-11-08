using System.Runtime.Versioning;
using NetGuard.Core.Logging;
using NetGuard.Core.Updates;
using NetGuard.Windows;

namespace NetGuard.Service;

/// <summary>
/// Runs self-healing: at start (so a lock survives reboots), every 30 seconds, and about a
/// second after anything in the firewall changes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GuardWorker : BackgroundService
{
    private readonly Guard _guard;
    private readonly ILogger<GuardWorker> _logger;
    private readonly SemaphoreSlim _wake = new(0);

    public GuardWorker(Guard guard, ILogger<GuardWorker> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { DataDirSecurity.Secure(_guard.Engine.Paths); }
        catch (Exception e) { _logger.LogWarning(e, "Could not secure the data folder"); }
        _guard.Engine.Log.Append(LogTypes.ServiceStarted, new { version = GitHubReleases.CurrentVersion.ToString() }, "NetGuard service");

        using var watcher = new FirewallWatcher(() => _wake.Release());

        while (!ct.IsCancellationRequested)
        {
            await Task.Run(_guard.Tick, ct);
            try
            {
                if (await _wake.WaitAsync(_guard.NextDelay(), ct))
                {
                    // Something changed in the firewall: wait a moment so a burst of changes is handled in one go.
                    await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
                    while (_wake.CurrentCount > 0) await _wake.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        try { _guard.Engine.Log.Append(LogTypes.ServiceStopped, new { reason = "service stopped" }, "NetGuard service"); } catch { }
        await base.StopAsync(ct);
    }
}
