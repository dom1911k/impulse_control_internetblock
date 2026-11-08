using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using NetGuard.Core.Ipc;

namespace NetGuard.Service;

/// <summary>
/// The named pipe the window app talks to. Any signed-in user may connect and ask; the service
/// records who asked and decides.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeServer : BackgroundService
{
    private readonly Guard _guard;
    private readonly ILogger<PipeServer> _logger;

    public PipeServer(Guard guard, ILogger<PipeServer> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    private static PipeSecurity CreateSecurity()
    {
        var ps = new PipeSecurity();
        // Clients open the pipe for GENERIC_READ|GENERIC_WRITE, which includes SYNCHRONIZE – without it, Windows refuses the connection.
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return ps;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var security = CreateSecurity();
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(IpcJson.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    64 * 1024, 64 * 1024, security);
                await server.WaitForConnectionAsync(ct);
                var s = server;
                server = null;
                _ = Task.Run(() => HandleClientAsync(s, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _logger.LogError(e, "Pipe server error");
                _guard.ReportServiceError("pipe-server", e.Message);
                await Task.Delay(1000, ct).ContinueWith(_ => { });
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));

                var line = await reader.ReadLineAsync(timeout.Token);
                if (line == null) return;

                IpcResponse response;
                var request = IpcJson.Deserialize<IpcRequest>(line);
                if (request == null)
                    response = IpcResponse.Fail("Malformed request.");
                else
                {
                    var caller = IdentifyCaller(pipe);
                    response = await Task.Run(() => _guard.Handle(request, caller), ct);
                }
                await writer.WriteLineAsync(IpcJson.Serialize(response).AsMemory(), ct);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // client went away – nothing to do
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while handling a client");
                _guard.ReportServiceError("pipe", e.Message);
            }
        }
    }

    /// <summary>Who is asking – for the log only; nothing depends on it.</summary>
    private static CallerInfo IdentifyCaller(NamedPipeServerStream pipe)
    {
        try { return new CallerInfo(pipe.GetImpersonationUserName()); }
        catch { return new CallerInfo("unknown user"); }
    }
}
