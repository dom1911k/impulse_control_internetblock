using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace NetGuard.Windows;

/// <summary>
/// Signals as soon as anything under the firewall policy registry key changes (rules added/removed,
/// firewall switched off…), so the service can heal within seconds instead of waiting for the
/// 30-second check. The periodic check stays as a safety net.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FirewallWatcher : IDisposable
{
    private const string PolicyKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
    private const int REG_NOTIFY_CHANGE_NAME = 0x1;
    private const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    private readonly Action _onChange;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    public FirewallWatcher(Action onChange)
    {
        _onChange = onChange;
        _thread = new Thread(Run) { IsBackground = true, Name = "NetGuard firewall watcher" };
        _thread.Start();
    }

    private void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(PolicyKey, writable: false);
                if (key == null) { Thread.Sleep(5000); continue; }
                using var evt = new AutoResetEvent(false);
                while (!_cts.IsCancellationRequested)
                {
                    var rc = RegNotifyChangeKeyValue(key.Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET,
                        evt.SafeWaitHandle, true);
                    if (rc != 0) throw new InvalidOperationException($"RegNotifyChangeKeyValue failed ({rc})");
                    if (WaitHandle.WaitAny(new[] { evt, _cts.Token.WaitHandle }) == 1) return;
                    _onChange();
                }
            }
            catch when (!_cts.IsCancellationRequested)
            {
                Thread.Sleep(5000); // fall back to the periodic check and retry
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(2000);
        _cts.Dispose();
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, bool bWatchSubtree, int dwNotifyFilter,
        SafeWaitHandle hEvent, bool fAsynchronous);
}
