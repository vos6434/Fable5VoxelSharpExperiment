using Voxel.Shared;

namespace Voxel.Shared.Tests;

public class NoiseGoldenTests
{
    [Fact]
    public void Simplex2_matches_typescript_bit_for_bit()
    {
        var g = Golden.Instance;
        var s2 = new Simplex2(g.Noise2Seed);
        foreach (var (x, y, raw, fbm) in g.Noise2Samples)
        {
            Assert.Equal(raw, s2.Noise(x, y));
            Assert.Equal(fbm, s2.Fbm(x / 40, y / 40, 4));
        }
    }

    [Fact]
    public void Simplex3_matches_typescript_bit_for_bit()
    {
        var g = Golden.Instance;
        var s3 = new Simplex3(g.Noise3Seed);
        foreach (var (x, y, z, value) in g.Noise3Samples)
        {
            Assert.Equal(value, s3.Noise(x, y, z));
        }
    }

    [Theory]
    [InlineData(2.5, 3)]
    [InlineData(-2.5, -2)]
    [InlineData(0.49999999999999994, 0)] // fp edge case: naive floor(x+0.5) gets this wrong
    [InlineData(-0.5, 0)]
    [InlineData(3.0, 3)]
    public void JsMath_Round_matches_ecmascript_semantics(double input, double expected)
    {
        Assert.Equal(expected, JsMath.Round(input));
    }
}
