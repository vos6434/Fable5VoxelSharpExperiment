using Silk.NET.OpenGL;
using Voxel.Client.Gl;

namespace Voxel.Client;

/// <summary>
/// Day/night state derived from world time: sun direction, sky gradient
/// colors, sun/moon disc colors, and the light intensity that drives world
/// shading and shadows. Pure computation (no GL) so it's unit-testable;
/// SkyRenderer draws it.
///
/// Convention (matches ClientClock): a day is dayLengthTicks long, tick 0 of
/// a day = 06:00 = sunrise. The sun travels a circle tilted ~20 deg off the
/// vertical (north bias) so noon shadows still have length.
/// </summary>
public readonly struct SkyState
{
    public readonly (float X, float Y, float Z) SunDir; // toward the sun (normalized)
    public readonly (float R, float G, float B) Zenith;
    public readonly (float R, float G, float B) Horizon;
    public readonly (float R, float G, float B) SunDiscColor;
    /// <summary>Directional light color × intensity (sun by day, moon by night).</summary>
    public readonly (float R, float G, float B) LightColor;
    /// <summary>0 at night, 1 at full day — drives ambient and shadow strength.</summary>
    public readonly float DayAmount;
    public readonly float MoonVisibility;
    /// <summary>Shadow-casting light direction: the sun while it's up, else the moon (opposite azimuth).</summary>
    public readonly (float X, float Y, float Z) DirLightDir;
    /// <summary>Shadow-casting light color × intensity (peak ~1 for the noon sun, ~0.25 cool for the moon).</summary>
    public readonly (float R, float G, float B) DirLightColor;

    public SkyState(
        (float, float, float) sunDir, (float, float, float) zenith, (float, float, float) horizon,
        (float, float, float) sunDisc, (float, float, float) light, float dayAmount, float moonVisibility,
        (float, float, float) dirLightDir, (float, float, float) dirLightColor)
    {
        SunDir = sunDir; Zenith = zenith; Horizon = horizon;
        SunDiscColor = sunDisc; LightColor = light; DayAmount = dayAmount; MoonVisibility = moonVisibility;
        DirLightDir = dirLightDir; DirLightColor = dirLightColor;
    }

    private static (float, float, float) Mix((float R, float G, float B) a, (float R, float G, float B) b, float t)
        => (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    internal static float Smoothstep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public static SkyState Compute(double worldTicks, int dayLengthTicks)
    {
        double dayFraction = (worldTicks % dayLengthTicks) / dayLengthTicks;
        if (dayFraction < 0) dayFraction += 1;
        double a = dayFraction * 2 * Math.PI;

        // Sun path: east/west sweep + vertical, tilted north so it never hits the zenith.
        float sx = (float)Math.Cos(a);
        float sy = (float)Math.Sin(a);
        const float tilt = 0.35f;
        float len = MathF.Sqrt(sx * sx + sy * sy + tilt * tilt);
        var sunDir = (sx / len, sy / len, tilt / len);
        float elevation = sunDir.Item2;

        float dayAmount = Smoothstep(-0.10f, 0.18f, elevation);
        float duskFactor = Math.Clamp(1f - Math.Abs(elevation) / 0.28f, 0f, 1f);

        var nightZenith = (0.02f, 0.03f, 0.08f);
        var nightHorizon = (0.05f, 0.06f, 0.13f);
        var dayZenith = (0.29f, 0.56f, 0.86f);
        var dayHorizon = (0.62f, 0.80f, 0.94f);
        var duskHorizon = (0.92f, 0.46f, 0.20f);

        var zenith = Mix(nightZenith, dayZenith, dayAmount);
        var horizon = Mix(nightHorizon, dayHorizon, dayAmount);
        horizon = Mix(horizon, duskHorizon, duskFactor * 0.85f);

        // Sun disc warms toward the horizon; sunlight intensity fades at dusk.
        var sunWarm = (1.0f, 0.55f, 0.25f);
        var sunWhite = (1.0f, 0.96f, 0.88f);
        var sunDisc = Mix(sunWarm, sunWhite, dayAmount);

        float sunIntensity = Smoothstep(-0.05f, 0.22f, elevation);
        var sunlight = (sunDisc.Item1 * sunIntensity, sunDisc.Item2 * sunIntensity, sunDisc.Item3 * sunIntensity);
        // Moonlight: cool, dim, fills in at night.
        float moonVis = 1f - dayAmount;
        var moonlight = (0.10f * moonVis, 0.12f * moonVis, 0.18f * moonVis);
        var light = (sunlight.Item1 + moonlight.Item1, sunlight.Item2 + moonlight.Item2, sunlight.Item3 + moonlight.Item3);

        // Shadow-casting directional light (plan 02 M7): the sun by day; at
        // night the moon takes over from the opposite azimuth at ~25% cool
        // intensity, so night scenes still have crisp (dimmer) shadows. Both
        // fade around the horizon; at dusk neither dominates (soft twilight).
        var moonDir = (-sunDir.Item1, -sunDir.Item2, sunDir.Item3);
        float sunF = Smoothstep(-0.02f, 0.15f, elevation);
        float moonF = Smoothstep(0.05f, 0.25f, -elevation);
        (float, float, float) dirLightDir;
        (float, float, float) dirLightColor;
        if (sunF >= moonF)
        {
            dirLightDir = sunDir;
            dirLightColor = (sunDisc.Item1 * sunF, sunDisc.Item2 * sunF, sunDisc.Item3 * sunF);
        }
        else
        {
            dirLightDir = moonDir;
            dirLightColor = (0.20f * moonF, 0.23f * moonF, 0.33f * moonF);
        }

        return new SkyState(sunDir, zenith, horizon, sunDisc, light, dayAmount, moonVis, dirLightDir, dirLightColor);
    }
}

/// <summary>Draws the sky gradient + sun/moon discs as a fullscreen background pass.</summary>
public sealed class SkyRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly GlShader _shader;
    private readonly uint _vao;

    public SkyRenderer(GL gl, string shaderDir)
    {
        _gl = gl;
        _shader = new GlShader(gl,
            File.ReadAllText(Path.Combine(shaderDir, "post.vert")),
            File.ReadAllText(Path.Combine(shaderDir, "sky.frag")));
        // Attribute-less VAO for the gl_VertexID fullscreen triangle: core
        // profile rejects draws with VAO 0 bound, and relying on whatever VAO
        // the previous pass left bound breaks whenever a mesh upload resets
        // the binding to 0 (black-sky flicker while chunks stream in).
        _vao = gl.GenVertexArray();
    }

    public void Render(
        in SkyState sky,
        (float X, float Y, float Z) forward, (float X, float Y, float Z) right, (float X, float Y, float Z) up,
        float tanHalfFov, float aspect)
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.BindVertexArray(_vao);
        _shader.Use();
        _shader.SetVec3("uCamForward", forward.X, forward.Y, forward.Z);
        _shader.SetVec3("uCamRight", right.X, right.Y, right.Z);
        _shader.SetVec3("uCamUp", up.X, up.Y, up.Z);
        _shader.SetFloat("uTanHalfFov", tanHalfFov);
        _shader.SetFloat("uAspect", aspect);
        _shader.SetVec3("uSkyZenith", sky.Zenith.R, sky.Zenith.G, sky.Zenith.B);
        _shader.SetVec3("uSkyHorizon", sky.Horizon.R, sky.Horizon.G, sky.Horizon.B);
        _shader.SetVec3("uSunDir", sky.SunDir.X, sky.SunDir.Y, sky.SunDir.Z);
        _shader.SetVec3("uSunDiscColor", sky.SunDiscColor.R, sky.SunDiscColor.G, sky.SunDiscColor.B);
        _shader.SetFloat("uMoonVisibility", sky.MoonVisibility);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
