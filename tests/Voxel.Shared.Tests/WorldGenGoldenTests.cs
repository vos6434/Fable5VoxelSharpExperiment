using System.Runtime.InteropServices;
using Voxel.Shared;

namespace Voxel.Shared.Tests;

public class WorldGenGoldenTests
{
    private static readonly Lazy<(BlockRegistry Blocks, ItemRegistry Items)> Registries =
        new(() => DataLoader.LoadRegistries(Path.Combine(Golden.RepoRoot, "data")));

    private static WorldGen NewGen() => new(Golden.Instance.Seed, Registries.Value.Blocks);

    [Fact]
    public void Palette_and_opacity_match()
    {
        var g = Golden.Instance;
        var blocks = Registries.Value.Blocks;
        Assert.Equal(g.Palette, blocks.Defs.Select(d => d.StringId).ToArray());
        Assert.Equal(g.Opaque, blocks.Opaque);
    }

    [Fact]
    public void Item_registry_matches()
    {
        var g = Golden.Instance;
        var items = Registries.Value.Items;
        Assert.Equal(g.ItemIds, items.Defs.Select(d => d.StringId).ToArray());
        Assert.Equal(g.ItemMaxStacks, items.Defs.Select(d => d.MaxStack).ToArray());
    }

    [Fact]
    public void Terrain_heights_boundaries_and_biomes_match()
    {
        var g = Golden.Instance;
        var gen = NewGen();
        for (int i = 0; i < g.TerrainSamples.Length; i++)
        {
            var s = g.TerrainSamples[i];
            Assert.Equal(s.Height, gen.SurfaceHeight(s.X, s.Z));
            Assert.Equal(s.HellBoundary, gen.HellBoundary(s.X, s.Z));
            Assert.Equal(s.SurfaceBiome, gen.GetSurfaceBiome(s.X, s.Z, s.Height).Name());
            // y values reconstructed the same way the dump script derived them.
            Assert.Equal(s.CaveBiome, gen.GetCaveBiome(s.X, -30 - i, s.Z).Name());
            Assert.Equal(s.BiomeAt, gen.BiomeAt(s.X, -20 - i * 4, s.Z));
        }
    }

    [Fact]
    public void Spawn_matches()
    {
        Assert.Equal(Golden.Instance.Spawn, NewGen().FindSpawn());
    }

    [Fact]
    public void Generated_chunks_match_byte_for_byte()
    {
        var g = Golden.Instance;
        var gen = NewGen();
        foreach (var sample in g.Chunks)
        {
            var result = gen.Generate(sample.Cx, sample.Cy, sample.Cz);
            Assert.Equal(sample.Empty, result.Empty);
            Assert.Equal(sample.Solid, result.Chunk.CountSolid());
            byte[] actual = MemoryMarshal.AsBytes<ushort>(result.Chunk.Blocks).ToArray();
            Assert.Equal(sample.Blocks, actual);
        }
    }
}
