using System.Diagnostics;
using Voxel.Shared;

namespace Voxel.Server;

/// <summary>
/// Pure tick-accumulator logic, separated from threading so it's unit-testable.
/// Fixed 20 TPS base rate scaled by a timescale (0 = paused); while paused,
/// explicit single-steps can be queued. Catch-up after stalls is capped so a
/// long GC pause doesn't fire a burst of hundreds of ticks.
/// </summary>
public sealed class ClockCore
{
    public const double TickSeconds = 1.0 / Constants.TicksPerSecond;
    public const int MaxCatchUpTicks = 5;
    public const float MaxTimescale = 10f;

    public long WorldTick;
    public float Timescale { get; private set; } = 1f;
    /// <summary>True when the last Advance had to drop backlog (running behind).</summary>
    public bool RunningBehind { get; private set; }

    private float _resumeScale = 1f;
    private double _accumulator;
    private int _pendingSteps;

    public bool Paused => Timescale == 0f;

    public void SetTimescale(float scale)
    {
        scale = Math.Clamp(scale, 0f, MaxTimescale);
        if (scale > 0f) _resumeScale = scale;
        Timescale = scale;
    }

    public void Pause()
    {
        if (Paused) return;
        _resumeScale = Timescale;
        Timescale = 0f;
    }

    public void Resume()
    {
        if (Paused) Timescale = _resumeScale;
    }

    /// <summary>Queue N single ticks to run while paused (ignored when running).</summary>
    public void RequestSteps(int n) => _pendingSteps += Math.Max(0, n);

    /// <summary>
    /// Consumes elapsed real time and returns how many ticks are due now.
    /// The caller increments WorldTick once per executed tick.
    /// </summary>
    public int Advance(double elapsedRealSeconds)
    {
        RunningBehind = false;
        if (Paused)
        {
            _accumulator = 0;
            int steps = _pendingSteps;
            _pendingSteps = 0;
            return steps;
        }

        _pendingSteps = 0;
        _accumulator += elapsedRealSeconds * Timescale;
        int due = 0;
        while (_accumulator >= TickSeconds && due < MaxCatchUpTicks)
        {
            _accumulator -= TickSeconds;
            due++;
        }
        if (_accumulator >= TickSeconds)
        {
            // Still behind after the cap: drop the backlog rather than spiral.
            RunningBehind = true;
            _accumulator = 0;
        }
        return due;
    }

    /// <summary>Seconds until the next tick is due (for the host's sleep).</summary>
    public double SecondsUntilNextTick()
    {
        if (Paused) return 0.05;
        return Math.Max(0, (TickSeconds - _accumulator) / Timescale);
    }
}

/// <summary>
/// Threaded host for ClockCore: runs the tick loop on a dedicated thread and
/// fires OnTick(worldTick) for each tick. Control methods are thread-safe;
/// tick handlers execute on the clock thread (the server's simulation thread).
/// </summary>
public sealed class WorldClock : IDisposable
{
    public ClockCore Core { get; } = new();

    /// <summary>Fired once per tick on the clock thread.</summary>
    public event Action<long>? OnTick;
    /// <summary>Fired after any timescale change (pause/resume/rate), on the caller's thread.</summary>
    public event Action? OnTimescaleChanged;

    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public long WorldTick { get { lock (_lock) return Core.WorldTick; } }
    public float Timescale { get { lock (_lock) return Core.Timescale; } }
    public bool Paused { get { lock (_lock) return Core.Paused; } }

    public void Start(long initialWorldTick)
    {
        lock (_lock) Core.WorldTick = initialWorldTick;
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "world-clock" };
        _thread.Start();
    }

    public void SetTimescale(float scale)
    {
        lock (_lock) Core.SetTimescale(scale);
        OnTimescaleChanged?.Invoke();
    }

    public void Pause()
    {
        lock (_lock) Core.Pause();
        OnTimescaleChanged?.Invoke();
    }

    public void Resume()
    {
        lock (_lock) Core.Resume();
        OnTimescaleChanged?.Invoke();
    }

    public void Step(int n)
    {
        lock (_lock) Core.RequestSteps(n);
    }

    private void RunLoop()
    {
        var stopwatch = Stopwatch.StartNew();
        double lastSeconds = 0;
        while (!_cts.IsCancellationRequested)
        {
            double now = stopwatch.Elapsed.TotalSeconds;
            double elapsed = now - lastSeconds;
            lastSeconds = now;

            int due;
            bool behind;
            lock (_lock)
            {
                due = Core.Advance(elapsed);
                behind = Core.RunningBehind;
            }
            if (behind)
            {
                Console.WriteLine("[server] tick loop running behind - dropping backlog");
            }

            for (int i = 0; i < due && !_cts.IsCancellationRequested; i++)
            {
                long tick;
                lock (_lock) tick = ++Core.WorldTick;
                OnTick?.Invoke(tick);
            }

            double sleepSeconds;
            lock (_lock) sleepSeconds = Core.SecondsUntilNextTick();
            int sleepMs = (int)Math.Clamp(sleepSeconds * 1000, 1, 20);
            Thread.Sleep(sleepMs);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(500);
    }
}
