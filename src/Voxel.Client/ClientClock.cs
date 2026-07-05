using Voxel.Shared;

namespace Voxel.Client;

/// <summary>
/// Client-side reconstruction of the server's world clock: anchors on each
/// TimeSync and extrapolates with real time × timescale between syncs. Small
/// disagreements are eased out over ~2 s instead of snapping (so the sun
/// never visibly jumps); large ones (server tick command, lag spike) snap.
/// </summary>
public sealed class ClientClock
{
    private const double ErrorEaseSeconds = 2.0;
    private const double SnapThresholdTicks = 60;

    private readonly Func<double> _now; // monotonic seconds

    private bool _synced;
    private long _anchorTick;
    private double _anchorTime;
    private float _timescale = 1f;
    private double _error;      // displayedAtSync - serverTick, eased to 0
    private double _errorTime;

    public int DayLengthTicks { get; private set; } = Constants.DayLengthTicks;
    public float Timescale => _timescale;
    public bool Synced => _synced;

    public ClientClock(Func<double>? now = null)
    {
        _now = now ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
    }

    public void OnSync(long worldTick, float timescale, int dayLengthTicks)
    {
        double displayedBefore = _synced ? WorldTicks : worldTick;
        _timescale = timescale;
        DayLengthTicks = dayLengthTicks;
        _anchorTick = worldTick;
        _anchorTime = _now();

        double error = displayedBefore - worldTick;
        _error = Math.Abs(error) > SnapThresholdTicks ? 0 : error;
        _errorTime = _anchorTime;
        _synced = true;
    }

    /// <summary>Continuous world time in ticks (fractional).</summary>
    public double WorldTicks
    {
        get
        {
            if (!_synced) return 0;
            double now = _now();
            double raw = _anchorTick + (now - _anchorTime) * Constants.TicksPerSecond * _timescale;
            double ease = Math.Max(0, 1 - (now - _errorTime) / ErrorEaseSeconds);
            return raw + _error * ease;
        }
    }

    /// <summary>"day N HH:MM" — Minecraft convention: tick 0 of a day is 06:00.</summary>
    public string Describe()
    {
        if (!_synced) return "time syncing...";
        double ticks = WorldTicks;
        long day = (long)(ticks / DayLengthTicks);
        double dayTicks = ticks - day * (double)DayLengthTicks;
        double dayFraction = dayTicks / DayLengthTicks;
        int totalMinutes = ((int)(dayFraction * 24 * 60) + 6 * 60) % (24 * 60);
        string pause = _timescale == 0 ? " (paused)" : _timescale != 1 ? $" x{_timescale:0.##}" : "";
        return $"day {day} {totalMinutes / 60:D2}:{totalMinutes % 60:D2}{pause}";
    }
}
