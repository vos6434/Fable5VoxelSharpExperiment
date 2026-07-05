namespace Voxel.Shared;

/// <summary>
/// JavaScript-compatible math helpers. Worldgen parity with the original
/// TypeScript implementation depends on these matching ECMAScript semantics
/// bit-for-bit, so don't "simplify" them to .NET defaults (e.g. Math.Round
/// uses banker's rounding; JS rounds ties toward +Infinity).
/// </summary>
public static class JsMath
{
    /// <summary>ECMAScript Math.round: nearest integer, ties toward +Infinity.</summary>
    public static double Round(double x)
    {
        double floor = Math.Floor(x);
        return x - floor >= 0.5 ? floor + 1 : floor;
    }
}
