using System.Windows.Threading;
using NetGuard.Core.Ipc;
using NetGuard.Windows;

namespace NetGuard.App;

/// <summary>Asks the service for its status every few seconds; the window and the tray icon listen.</summary>
public sealed class StatusMonitor
{
    private readonly GuardClient _client = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _refreshing;

    public StatusDto? Status { get; private set; }
    public string Problem { get; private set; } = "";
    public bool ServiceDown => Status == null;
    public bool HasLoaded { get; private set; }
    /// <summary>Difference between NetGuard's trusted time and this PC's clock.</summary>
    public TimeSpan ServerOffset { get; private set; }

    public event Action? Changed;

    public StatusMonitor() => _timer.Tick += async (_, _) => await RefreshAsync();

    public void Start() => _timer.Start();

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var r = await _client.SendAsync(new IpcRequest { Command = IpcCommands.Status }, TimeSpan.FromSeconds(10));
            Status = r.Status;
            Problem = "";
            if (r.Status != null) ServerOffset = r.Status.ServerNowUtc - DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            Status = null;
            Problem = ex.Message;
        }
        finally
        {
            _refreshing = false;
            HasLoaded = true;
        }
        Changed?.Invoke();
    }
}
