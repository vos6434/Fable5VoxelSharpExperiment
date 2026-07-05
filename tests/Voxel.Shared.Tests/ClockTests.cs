using Voxel.Client;
using Voxel.Server;
using Voxel.Shared;

namespace Voxel.Shared.Tests;

public class ClockCoreTests
{
    private static int AdvanceInSlices(ClockCore core, double totalSeconds, double slice = 0.01)
    {
        int ticks = 0;
        double remaining = totalSeconds;
        while (remaining > 1e-9)
        {
            double step = Math.Min(slice, remaining);
            remaining -= step;
            int due = core.Advance(step);
            core.WorldTick += due;
            ticks += due;
        }
        return ticks;
    }

    [Fact]
    public void One_second_at_normal_speed_is_20_ticks()
    {
        // Sample half a tick past the boundary: exactly 1.0 s sits on a
        // floating-point knife edge (20 x 1/20 in doubles is > 1.0).
        var core = new ClockCore();
        Assert.Equal(20, AdvanceInSlices(core, 1.025));
        Assert.Equal(20, core.WorldTick);
    }

    [Fact]
    public void Timescale_scales_tick_production()
    {
        var core = new ClockCore();
        core.SetTimescale(5f);
        Assert.Equal(100, AdvanceInSlices(core, 1.005));
        core.SetTimescale(0.5f);
        Assert.Equal(10, AdvanceInSlices(core, 1.01));
    }

    [Fact]
    public void Pause_stops_ticks_and_steps_run_exactly_n()
    {
        var core = new ClockCore();
        core.Pause();
        Assert.Equal(0, AdvanceInSlices(core, 2.0));
        core.RequestSteps(3);
        int due = core.Advance(0.01);
        Assert.Equal(3, due);
        Assert.Equal(0, core.Advance(0.01)); // steps are one-shot
    }

    [Fact]
    public void Resume_restores_previous_rate()
    {
        var core = new ClockCore();
        core.SetTimescale(4f);
        core.Pause();
        Assert.True(core.Paused);
        core.Resume();
        Assert.Equal(4f, core.Timescale);
    }

    [Fact]
    public void Long_stall_is_capped_and_reports_running_behind()
    {
        var core = new ClockCore();
        int due = core.Advance(10.0); // 200 ticks owed
        Assert.Equal(ClockCore.MaxCatchUpTicks, due);
        Assert.True(core.RunningBehind);
        // Backlog was dropped: the next small advance produces a normal tick count.
        Assert.Equal(1, core.Advance(0.05));
    }

    [Fact]
    public void Timescale_is_clamped()
    {
        var core = new ClockCore();
        core.SetTimescale(999f);
        Assert.Equal(ClockCore.MaxTimescale, core.Timescale);
        core.SetTimescale(-3f);
        Assert.Equal(0f, core.Timescale);
    }
}

public class ClientClockTests
{
    [Fact]
    public void Reconstructs_time_between_syncs()
    {
        double now = 100.0;
        var clock = new ClientClock(() => now);
        clock.OnSync(1000, 1f, Constants.DayLengthTicks);
        now += 2.0; // 2 real seconds at x1 = 40 ticks
        Assert.Equal(1040, clock.WorldTicks, 1);
    }

    [Fact]
    public void Timescale_affects_extrapolation()
    {
        double now = 0;
        var clock = new ClientClock(() => now);
        clock.OnSync(0, 5f, Constants.DayLengthTicks);
        now += 1.0;
        Assert.Equal(100, clock.WorldTicks, 1);
    }

    [Fact]
    public void Small_corrections_ease_instead_of_snapping()
    {
        double now = 0;
        var clock = new ClientClock(() => now);
        clock.OnSync(0, 1f, Constants.DayLengthTicks);
        now += 1.0; // client believes tick 20
        clock.OnSync(30, 1f, Constants.DayLengthTicks); // server says 30 (10 ahead)
        // Immediately after the sync, display is continuous (still ~20)...
        Assert.Equal(20, clock.WorldTicks, 1);
        // ...and converges to server truth after the ease window.
        now += 3.0;
        Assert.Equal(90, clock.WorldTicks, 1);
    }

    [Fact]
    public void Large_corrections_snap()
    {
        double now = 0;
        var clock = new ClientClock(() => now);
        clock.OnSync(0, 1f, Constants.DayLengthTicks);
        now += 1.0;
        clock.OnSync(5000, 1f, Constants.DayLengthTicks); // tick command jumped time
        Assert.Equal(5000, clock.WorldTicks, 1);
    }

    [Fact]
    public void Describe_uses_minecraft_morning_convention()
    {
        double now = 0;
        var clock = new ClientClock(() => now);
        clock.OnSync(0, 1f, 24000);
        Assert.StartsWith("day 0 06:00", clock.Describe());
        clock.OnSync(6000, 1f, 24000); // quarter day later = noon
        Assert.StartsWith("day 0 12:00", clock.Describe());
        clock.OnSync(24000, 0f, 24000);
        Assert.Contains("day 1", clock.Describe());
        Assert.Contains("(paused)", clock.Describe());
    }
}

public class TimeSyncProtocolTests
{
    [Fact]
    public void TimeSync_round_trips()
    {
        byte[] encoded = Protocol.EncodeTimeSync(123456789012345, 2.5f, 24000);
        Assert.Equal(Msg.TimeSync, Protocol.TypeOf(encoded));
        Assert.Equal(17, encoded.Length);
        var (tick, scale, dayLength) = Protocol.DecodeTimeSync(encoded);
        Assert.Equal(123456789012345, tick);
        Assert.Equal(2.5f, scale);
        Assert.Equal(24000, dayLength);
    }

    [Fact]
    public void Hello_and_welcome_carry_protocol_version()
    {
        byte[] hello = Protocol.EncodeJson(Msg.Hello, new HelloPayload { Name = "steve", ProtocolVersion = Protocol.Version });
        var decoded = Protocol.DecodeJson<HelloPayload>(hello);
        Assert.Equal(Protocol.Version, decoded.ProtocolVersion);

        // A pre-versioning hello (no field) decodes as version 0 → rejectable.
        byte[] legacy = [(byte)Msg.Hello, .. "{\"name\":\"old\"}"u8];
        Assert.Equal(0, Protocol.DecodeJson<HelloPayload>(legacy).ProtocolVersion);
    }
}
