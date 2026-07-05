using System.Text.Json;
using Voxel.Shared;

namespace Voxel.Shared.Tests;

/// <summary>
/// Reference values dumped from the TypeScript implementation
/// (D:\Fable5Voxel tools/dump-golden.ts). The port must reproduce them
/// bit-for-bit: noise and worldgen are transcendental-free, so exact
/// cross-language equality is the contract, not an aspiration.
/// </summary>
public sealed class Golden
{
    public static Golden Instance { get; } = Load();

    public required string[] Palette { get; init; }
    public required byte[] Opaque { get; init; }
    public required string[] ItemIds { get; init; }
    public required int[] ItemMaxStacks { get; init; }
    public required int Seed { get; init; }
    public required (int X, int Y, int Z) Spawn { get; init; }
    public required int Noise2Seed { get; init; }
    public required (double X, double Y, double Raw, double Fbm)[] Noise2Samples { get; init; }
    public required int Noise3Seed { get; init; }
    public required (double X, double Y, double Z, double Value)[] Noise3Samples { get; init; }
    public required TerrainSample[] TerrainSamples { get; init; }
    public required ChunkSample[] Chunks { get; init; }
    public required Dictionary<string, byte[]> Protocol { get; init; }

    public sealed record TerrainSample(
        double X, double Z, int Height, double HellBoundary,
        string SurfaceBiome, string CaveBiome, string BiomeAt);

    public sealed record ChunkSample(int Cx, int Cy, int Cz, bool Empty, int Solid, byte[] Blocks);

    /// <summary>Repo root (contains the .sln and /data), found from the test assembly location.</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "Fable5VoxelSharp.slnx")) &&
               !File.Exists(Path.Combine(dir.FullName, "Fable5VoxelSharp.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Fable5VoxelSharp solution not found above test directory");
    }

    private static Golden Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "golden", "golden.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var spawn = root.GetProperty("spawn");
        return new Golden
        {
            Palette = [.. root.GetProperty("palette").EnumerateArray().Select(e => e.GetString()!)],
            Opaque = [.. root.GetProperty("opaque").EnumerateArray().Select(e => (byte)e.GetInt32())],
            ItemIds = [.. root.GetProperty("itemIds").EnumerateArray().Select(e => e.GetString()!)],
            ItemMaxStacks = [.. root.GetProperty("itemMaxStacks").EnumerateArray().Select(e => e.GetInt32())],
            Seed = root.GetProperty("seed").GetInt32(),
            Spawn = (spawn.GetProperty("x").GetInt32(), spawn.GetProperty("y").GetInt32(), spawn.GetProperty("z").GetInt32()),
            Noise2Seed = root.GetProperty("noise2Seed").GetInt32(),
            Noise2Samples = [.. root.GetProperty("noise2Samples").EnumerateArray().Select(row =>
            {
                var a = row.EnumerateArray().ToArray();
                return (a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble(), a[3].GetDouble());
            })],
            Noise3Seed = root.GetProperty("noise3Seed").GetInt32(),
            Noise3Samples = [.. root.GetProperty("noise3Samples").EnumerateArray().Select(row =>
            {
                var a = row.EnumerateArray().ToArray();
                return (a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble(), a[3].GetDouble());
            })],
            TerrainSamples = [.. root.GetProperty("terrainSamples").EnumerateArray().Select(row =>
            {
                var a = row.EnumerateArray().ToArray();
                return new TerrainSample(
                    a[0].GetDouble(), a[1].GetDouble(), a[2].GetInt32(), a[3].GetDouble(),
                    a[4].GetString()!, a[5].GetString()!, a[6].GetString()!);
            })],
            Chunks = [.. root.GetProperty("chunks").EnumerateArray().Select(c => new ChunkSample(
                c.GetProperty("cx").GetInt32(),
                c.GetProperty("cy").GetInt32(),
                c.GetProperty("cz").GetInt32(),
                c.GetProperty("empty").GetBoolean(),
                c.GetProperty("solid").GetInt32(),
                Convert.FromBase64String(c.GetProperty("blocks").GetString()!)))],
            Protocol = root.GetProperty("protocol").EnumerateObject()
                .ToDictionary(p => p.Name, p => Convert.FromBase64String(p.Value.GetString()!)),
        };
    }
}
