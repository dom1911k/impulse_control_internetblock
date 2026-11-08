using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using NetGuard.Core.Ipc;

namespace NetGuard.Windows;

/// <summary>Talks to the NetGuard service over the named pipe. Used by the window app.</summary>
[SupportedOSPlatform("windows")]
public sealed class GuardClient
{
    public sealed class ServiceUnavailableException : Exception
    {
        public ServiceUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
    }

    public async Task<IpcResponse> SendAsync(IpcRequest request, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));

        // Identification lets the service see who is asking (and whether they are an elevated admin).
        using var pipe = new NamedPipeClientStream(".", IpcJson.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(2000, cts.Token);
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            throw new ServiceUnavailableException("The NetGuard service isn't running.", e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new ServiceUnavailableException("The NetGuard service is running but refused the connection (access denied).", e);
        }

        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync(IpcJson.Serialize(request).AsMemory(), cts.Token);
        var line = await reader.ReadLineAsync(cts.Token)
                   ?? throw new ServiceUnavailableException("The NetGuard service closed the connection.");
        return IpcJson.Deserialize<IpcResponse>(line) ?? throw new InvalidDataException("Empty answer from the service.");
    }

    /// <summary>Send and throw with the service's message if it refused.</summary>
    public async Task<IpcResponse> CallAsync(IpcRequest request, TimeSpan? timeout = null)
    {
        var r = await SendAsync(request, timeout);
        if (!r.Ok) throw new InvalidOperationException(r.Error ?? "The service refused the request.");
        return r;
    }
}
