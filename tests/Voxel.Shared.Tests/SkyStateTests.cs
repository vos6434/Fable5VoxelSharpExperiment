using Voxel.Client;

namespace Voxel.Shared.Tests;

public class SkyStateTests
{
    private const int Day = 24000;

    private static float Length((float X, float Y, float Z) v)
        => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    [Fact]
    public void Sun_direction_is_normalized_across_the_day()
    {
        for (int t = 0; t < Day; t += 500)
        {
            var sky = SkyState.Compute(t, Day);
            Assert.Equal(1f, Length(sky.SunDir), 3);
        }
    }

    [Fact]
    public void Sun_is_high_at_noon_and_below_at_midnight()
    {
        var noon = SkyState.Compute(6000, Day);   // 12:00
        var midnight = SkyState.Compute(18000, Day); // 00:00
        Assert.True(noon.SunDir.Y > 0.9f, $"noon elevation {noon.SunDir.Y}");
        Assert.True(midnight.SunDir.Y < -0.9f, $"midnight elevation {midnight.SunDir.Y}");
    }

    [Fact]
    public void Sun_is_on_the_horizon_at_dawn_and_dusk()
    {
        Assert.Equal(0f, SkyState.Compute(0, Day).SunDir.Y, 2);      // 06:00
        Assert.Equal(0f, SkyState.Compute(12000, Day).SunDir.Y, 2);  // 18:00
    }

    [Fact]
    public void Day_amount_tracks_the_sun()
    {
        Assert.True(SkyState.Compute(6000, Day).DayAmount > 0.98f);   // noon: full day
        Assert.True(SkyState.Compute(18000, Day).DayAmount < 0.02f);  // midnight: full night
    }

    [Fact]
    public void Moon_is_visible_only_at_night()
    {
        Assert.True(SkyState.Compute(18000, Day).MoonVisibility > 0.9f);
        Assert.True(SkyState.Compute(6000, Day).MoonVisibility < 0.1f);
    }

    [Fact]
    public void Light_is_bright_by_day_and_dim_by_night()
    {
        var noon = SkyState.Compute(6000, Day).LightColor;
        var midnight = SkyState.Compute(18000, Day).LightColor;
        float noonLum = noon.R + noon.G + noon.B;
        float nightLum = midnight.R + midnight.G + midnight.B;
        Assert.True(noonLum > 2.0f, $"noon light {noonLum}");
        Assert.True(nightLum < 0.6f, $"night light {nightLum}");
    }

    [Fact]
    public void Negative_and_wrapped_times_are_handled()
    {
        // Day N noon should equal day 0 noon.
        var a = SkyState.Compute(6000, Day);
        var b = SkyState.Compute(6000 + 5 * Day, Day);
        Assert.Equal(a.SunDir.Y, b.SunDir.Y, 4);
    }
}
