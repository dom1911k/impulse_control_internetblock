namespace NetGuard.Core;

/// <summary>
/// Cross-process lock so only one NetGuard process changes the firewall at a time
/// (window app, ngctl, the dead-man task and – from milestone 2 – the service).
/// Implemented as an exclusive handle on a file in NetGuard's data folder: it works for SYSTEM and
/// administrators alike, and Windows releases it automatically if the process crashes.
/// </summary>
public sealed class EngineLock : IDisposable
{
    private readonly FileStream _stream;

    private EngineLock(FileStream s) => _stream = s;

    public static EngineLock Acquire(NetGuardPaths paths, TimeSpan? timeout = null)
    {
        Directory.CreateDirectory(paths.DataDir);
        var path = Path.Combine(paths.DataDir, "engine.lock");
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
        var delay = 50;
        while (true)
        {
            try
            {
                return new EngineLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 500);
            }
            catch (IOException e)
            {
                throw new TimeoutException("Another NetGuard process is busy changing the firewall. Please try again in a moment.", e);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
