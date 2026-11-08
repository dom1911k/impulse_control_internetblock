namespace NetGuard.Core.Rules;

/// <summary>
/// NetGuard's own notion of "now". It follows the Windows clock only while that moves at the
/// same pace as real elapsed time; jumps (someone changing the clock to end a cooldown early)
/// are ignored and reported. It never runs backwards.
/// </summary>
public sealed class TrustedClock
{
    public static readonly TimeSpan JumpThreshold = TimeSpan.FromMinutes(2);

    private readonly Func<DateTimeOffset> _wall;
    private readonly Func<long> _monoMs;
    private DateTimeOffset _trusted;
    private long _lastMono;

    /// <summary>Raised with (how far the Windows clock jumped).</summary>
    public event Action<TimeSpan>? JumpDetected;

    public TrustedClock(Func<DateTimeOffset> wall, Func<long> monotonicMs, DateTimeOffset? persisted)
    {
        _wall = wall;
        _monoMs = monotonicMs;
        var w = wall();
        _trusted = persisted is { } p && p > w ? p : w; // never go back behind what we knew
        _lastMono = monotonicMs();
    }

    public static TrustedClock System(DateTimeOffset? persisted) =>
        new(() => DateTimeOffset.UtcNow, () => Environment.TickCount64, persisted);

    public DateTimeOffset Now
    {
        get
        {
            lock (this)
            {
                var mono = _monoMs();
                var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, mono - _lastMono));
                _lastMono = mono;
                var expected = _trusted + elapsed;
                var wall = _wall();
                var drift = wall - expected;
                if (drift.Duration() > JumpThreshold)
                {
                    _trusted = expected;
                    JumpDetected?.Invoke(drift);
                }
                else
                {
                    _trusted = wall > _trusted ? wall : _trusted; // follow small corrections (time sync), never backwards
                }
                return _trusted;
            }
        }
    }
}
