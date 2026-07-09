using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Voxel.Server;
using Voxel.Shared;

namespace Voxel.Shared.Tests;

/// <summary>Majority-vote downsampling correctness (plan 04 M1).</summary>
public class ChunkLodTests
{
    private const ushort Stone = 1;
    private const ushort Dirt = 2;
    private const ushort Water = 9;

    private static ushort[] Chunk(ushort fill = 0)
    {
        var blocks = new ushort[Constants.ChunkVolume];
        if (fill != 0) Array.Fill(blocks, fill);
        return blocks;
    }

    private static void FillBox(ushort[] blocks, int x0, int y0, int z0, int x1, int y1, int z1, ushort id)
    {
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
        {
            blocks[ChunkData.Index(x, y, z)] = id;
        }
    }

    [Fact]
    public void Uniform_chunk_downsamples_to_uniform_cells()
    {
        foreach (int level in (int[])[1, 2])
        {
            ushort[] cells = ChunkLod.Downsample(Chunk(Stone), level, Water);
            Assert.Equal(ChunkLod.CellCount(level), cells.Length);
            Assert.All(cells, id => Assert.Equal(Stone, id));
        }
        Assert.Equal(512, ChunkLod.CellCount(1));
        Assert.Equal(64, ChunkLod.CellCount(2));
    }

    [Fact]
    public void Half_solid_cell_stays_solid()
    {
        // Flat slab: bottom half of every level-1 cell in the y=0 row.
        var blocks = Chunk();
        FillBox(blocks, 0, 0, 0, 15, 0, 15, Stone);
        ushort[] cells = ChunkLod.Downsample(blocks, 1, Water);
        for (int i = 0; i < 64; i++) Assert.Equal(Stone, cells[i]);   // y=0 cell row
        for (int i = 64; i < 512; i++) Assert.Equal(0, cells[i]);     // air above
    }

    [Fact]
    public void Sub_half_solid_sliver_erases()
    {
        var blocks = Chunk();
        blocks[ChunkData.Index(4, 4, 4)] = Stone; // 1 of 8 in its level-1 cell
        ushort[] cells = ChunkLod.Downsample(blocks, 1, Water);
        Assert.All(cells, id => Assert.Equal(0, id));

        // Same sliver at level 2: a full 2×2×2 blob is 8 of 64 — still erased.
        FillBox(blocks, 4, 4, 4, 5, 5, 5, Stone);
        Assert.All(ChunkLod.Downsample(blocks, 2, Water), id => Assert.Equal(0, id));
    }

    [Fact]
    public void Water_wins_only_without_a_solid_majority()
    {
        // Half water, half air → water (ocean surface cells survive).
        var blocks = Chunk();
        FillBox(blocks, 0, 0, 0, 1, 0, 1, Water);
        Assert.Equal(Water, ChunkLod.Downsample(blocks, 1, Water)[0]);

        // Half water, half stone → stone (shoreline slivers drop the water).
        FillBox(blocks, 0, 1, 0, 1, 1, 1, Stone);
        Assert.Equal(Stone, ChunkLod.Downsample(blocks, 1, Water)[0]);

        // 3 water, 5 air → air (below half, water counts as air).
        var sparse = Chunk();
        sparse[ChunkData.Index(0, 0, 0)] = Water;
        sparse[ChunkData.Index(1, 0, 0)] = Water;
        sparse[ChunkData.Index(0, 1, 0)] = Water;
        Assert.Equal(0, ChunkLod.Downsample(sparse, 1, Water)[0]);
    }

    [Fact]
    public void Solid_tie_breaks_to_lower_id()
    {
        var blocks = Chunk();
        FillBox(blocks, 0, 0, 0, 1, 0, 1, Dirt);  // 4 dirt
        FillBox(blocks, 0, 1, 0, 1, 1, 1, Stone); // 4 stone
        Assert.Equal(Stone, ChunkLod.Downsample(blocks, 1, Water)[0]);
    }

    [Fact]
    public void Cell_index_order_matches_chunk_layout()
    {
        // Fill exactly the source region of level-1 cell (x=3, y=5, z=2).
        var blocks = Chunk();
        FillBox(blocks, 6, 10, 4, 7, 11, 5, Stone);
        ushort[] cells = ChunkLod.Downsample(blocks, 1, Water);
        Assert.Equal(Stone, cells[(5 * 8 + 2) * 8 + 3]);
        Assert.Equal(1, cells.Count(id => id != 0));
    }
}

/// <summary>LOD blob caching and edit invalidation in the world store (plan 04 M1).</summary>
public class LodStoreTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string path in _tempPaths)
        {
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* WAL may linger briefly */ }
    }

    private string TempDb() =>
        _tempPaths.Append(Path.Combine(Path.GetTempPath(), $"voxel-lod-{Guid.NewGuid():N}.db")).Last();

    private static ushort[] Inflate(byte[] blob, int cellCount)
    {
        using var input = new MemoryStream(blob);
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        byte[] raw = output.ToArray();
        Assert.Equal(cellCount * 2, raw.Length);
        var cells = new ushort[cellCount];
        Buffer.BlockCopy(raw, 0, cells, 0, raw.Length);
        return cells;
    }

    [Fact]
    public void Lod_blob_matches_downsampled_source_and_survives_reopen()
    {
        string path = TempDb();
        var blocks = DataLoader.LoadRegistries(FindDataDir()).Blocks;
        var generator = new WorldGen(WorldGen.DefaultSeed, blocks);
        ushort water = blocks.Resolve("water");

        ushort[]? FetchCells(WorldStore s, int level, int cx, int cy, int cz)
        {
            byte[]? blob = s.LoadLodBlob(level, cx, cy, cz);
            return blob is null ? null : Inflate(blob, ChunkLod.CellCount(level));
        }

        // All-air downsamples are served as null (empty), so expectations normalize the same way.
        static ushort[]? Normalize(ushort[] cells) => cells.All(id => id == 0) ? null : cells;

        ushort[]? expected1, expected2;
        using (var store = new WorldStore(path, generator, blocks))
        {
            var source = store.Load(0, 0, 0);
            expected1 = Normalize(ChunkLod.Downsample(source.Blocks, 1, water));
            expected2 = Normalize(ChunkLod.Downsample(source.Blocks, 2, water));
            Assert.NotNull(expected1); // chunk (0,0,0) has terrain at LOD1 — keeps the test meaningful

            Assert.Equal(expected1, FetchCells(store, 1, 0, 0, 0));
            Assert.Equal(expected2, FetchCells(store, 2, 0, 0, 0));
        }

        // Reopen: blobs come from the lods table (memory cache is gone).
        using (var store = new WorldStore(path, generator, blocks))
        {
            Assert.Equal(expected1, FetchCells(store, 1, 0, 0, 0));
            Assert.Equal(expected2, FetchCells(store, 2, 0, 0, 0));
        }
    }

    [Fact]
    public void Edit_invalidates_cached_lods()
    {
        string path = TempDb();
        var blocks = DataLoader.LoadRegistries(FindDataDir()).Blocks;
        var generator = new WorldGen(WorldGen.DefaultSeed, blocks);
        ushort stone = blocks.Resolve("stone");
        ushort water = blocks.Resolve("water");

        using var store = new WorldStore(path, generator, blocks);
        store.LoadLodBlob(1, 0, 0, 0); // prime memory + DB caches

        // Fill level-1 cell (0, 7, 0) of chunk (0,0,0): world y 14..15.
        for (int y = 14; y <= 15; y++)
        for (int z = 0; z <= 1; z++)
        for (int x = 0; x <= 1; x++)
        {
            store.SetBlock(x, y, z, stone);
        }

        byte[]? blob = store.LoadLodBlob(1, 0, 0, 0);
        Assert.NotNull(blob);
        ushort[] cells = Inflate(blob, ChunkLod.CellCount(1));
        Assert.Equal(stone, cells[(7 * 8 + 0) * 8 + 0]);
        Assert.Equal(ChunkLod.Downsample(store.Load(0, 0, 0).Blocks, 1, water), cells);
    }

    [Fact]
    public void Out_of_band_chunks_serve_empty_without_generating()
    {
        string path = TempDb();
        var blocks = DataLoader.LoadRegistries(FindDataDir()).Blocks;
        var generator = new WorldGen(WorldGen.DefaultSeed, blocks);

        using var store = new WorldStore(path, generator, blocks);
        int before = store.ChunkCount;
        Assert.Null(store.LoadLodBlob(1, 0, ChunkLod.BandMaxCy + 1, 0));
        Assert.Null(store.LoadLodBlob(2, 0, ChunkLod.BandMinCy - 1, 0));
        Assert.Equal(before, store.ChunkCount); // no source chunks were generated
    }

    private static string FindDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "data");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("/data not found");
    }
}
