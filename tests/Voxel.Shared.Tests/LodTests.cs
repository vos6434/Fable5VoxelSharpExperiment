using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Voxel.Server;
using Voxel.Shared;

namespace Voxel.Shared.Tests;

/// <summary>Voxy-style mip correctness (plan 04 v2 M2): most opaque of 8 children, ties toward the top.</summary>
public class ChunkLodTests
{
    private const ushort Stone = 1;
    private const ushort Grass = 2;
    private const ushort Water = 3;

    // Rank: air < translucent (water) < opaque (stone, grass).
    private static readonly byte[] Opaque = [0, 1, 1, 0];

    private static ushort[] Grid(ushort fill = 0)
    {
        var cells = new ushort[Constants.ChunkVolume];
        if (fill != 0) Array.Fill(cells, fill);
        return cells;
    }

    private static void FillBox(ushort[] cells, int x0, int y0, int z0, int x1, int y1, int z1, ushort id)
    {
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
        {
            cells[ChunkData.Index(x, y, z)] = id;
        }
    }

    private static ushort[]?[] Children(params (int Dx, int Dy, int Dz, ushort[] Grid)[] filled)
    {
        var children = new ushort[]?[8];
        foreach (var (dx, dy, dz, grid) in filled)
        {
            children[ChunkLod.ChildIndex(dx, dy, dz)] = grid;
        }
        return children;
    }

    [Fact]
    public void Uniform_children_mip_to_uniform_parent()
    {
        var children = new ushort[]?[8];
        for (int i = 0; i < 8; i++) children[i] = Grid(Stone);
        ushort[]? cells = ChunkLod.MipSections(children, Opaque);
        Assert.NotNull(cells);
        Assert.All(cells, id => Assert.Equal(Stone, id));
    }

    [Fact]
    public void All_air_children_mip_to_null()
    {
        Assert.Null(ChunkLod.MipSections(new ushort[]?[8], Opaque));
        Assert.Null(ChunkLod.MipSections(Children((0, 0, 0, Grid())), Opaque));
    }

    [Fact]
    public void Grass_top_survives_over_dirt()
    {
        // A grass layer over stone: the 2×2×2 source of each surface cell has
        // 4 stone below, 4 grass on top — equal opacity, topmost corner wins.
        var child = Grid();
        FillBox(child, 0, 0, 0, 15, 0, 15, Stone);
        FillBox(child, 0, 1, 0, 15, 1, 15, Grass);
        ushort[]? cells = ChunkLod.MipSections(Children((0, 0, 0, child)), Opaque);
        Assert.NotNull(cells);
        for (int z = 0; z < 8; z++)
        for (int x = 0; x < 8; x++)
        {
            Assert.Equal(Grass, cells[ChunkData.Index(x, 0, z)]);
        }
    }

    [Fact]
    public void Opacity_ranks_opaque_over_translucent_over_air()
    {
        // 1 stone + 7 water → stone (opaque beats translucent, count irrelevant).
        var child = Grid();
        FillBox(child, 0, 0, 0, 1, 1, 1, Water);
        child[ChunkData.Index(0, 0, 0)] = Stone;
        ushort[]? cells = ChunkLod.MipSections(Children((0, 0, 0, child)), Opaque);
        Assert.NotNull(cells);
        Assert.Equal(Stone, cells[ChunkData.Index(0, 0, 0)]);

        // 1 water + 7 air → water (never erase the only non-air block).
        var sparse = Grid();
        sparse[ChunkData.Index(2, 2, 2)] = Water;
        cells = ChunkLod.MipSections(Children((0, 0, 0, sparse)), Opaque);
        Assert.NotNull(cells);
        Assert.Equal(Water, cells[ChunkData.Index(1, 1, 1)]);
    }

    [Fact]
    public void Single_block_never_erases()
    {
        // The Voxy rule keeps thin features: 1 solid of 8 still wins the cell.
        var child = Grid();
        child[ChunkData.Index(5, 9, 3)] = Stone;
        ushort[]? cells = ChunkLod.MipSections(Children((0, 0, 0, child)), Opaque);
        Assert.NotNull(cells);
        Assert.Equal(Stone, cells[ChunkData.Index(2, 4, 1)]);
        Assert.Equal(1, cells.Count(id => id != 0));
    }

    [Fact]
    public void Children_land_in_their_octants()
    {
        // Child (1,0,1) full stone → parent cells x 8..15, y 0..7, z 8..15.
        ushort[]? cells = ChunkLod.MipSections(Children((1, 0, 1, Grid(Stone))), Opaque);
        Assert.NotNull(cells);
        for (int y = 0; y < 16; y++)
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            ushort expected = (ushort)(x >= 8 && y < 8 && z >= 8 ? Stone : 0);
            Assert.Equal(expected, cells[ChunkData.Index(x, y, z)]);
        }
    }
}

/// <summary>Section pyramid caching and edit invalidation in the world store (plan 04 v2 M2).</summary>
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
            TryDelete(path + ".lock");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* WAL may linger briefly */ }
    }

    private string TempDb() =>
        _tempPaths.Append(Path.Combine(Path.GetTempPath(), $"voxel-lod-{Guid.NewGuid():N}.db")).Last();

    private static ushort[] Inflate(byte[] blob)
    {
        using var input = new MemoryStream(blob);
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        byte[] raw = output.ToArray();
        Assert.Equal(Constants.ChunkVolume * 2, raw.Length);
        var cells = new ushort[Constants.ChunkVolume];
        Buffer.BlockCopy(raw, 0, cells, 0, raw.Length);
        return cells;
    }

    private static ushort[]? FetchCells(WorldStore store, int level, int sx, int sy, int sz)
    {
        byte[]? blob = store.LoadLodBlob(level, sx, sy, sz);
        return blob is null ? null : Inflate(blob);
    }

    /// <summary>Reference mip of a level-1 section straight from the store's chunks.</summary>
    private static ushort[]? ExpectedLevel1(WorldStore store, BlockRegistry blocks, int sx, int sy, int sz)
    {
        var children = new ushort[]?[8];
        for (int dy = 0; dy < 2; dy++)
        for (int dz = 0; dz < 2; dz++)
        for (int dx = 0; dx < 2; dx++)
        {
            var chunk = store.Load(sx * 2 + dx, sy * 2 + dy, sz * 2 + dz);
            children[ChunkLod.ChildIndex(dx, dy, dz)] = chunk.CountSolid() == 0 ? null : chunk.Blocks;
        }
        return ChunkLod.MipSections(children, blocks.Opaque);
    }

    [Fact]
    public void Level1_and_level2_mip_from_below_and_survive_reopen()
    {
        string path = TempDb();
        var blocks = DataLoader.LoadRegistries(FindDataDir()).Blocks;
        var generator = new WorldGen(WorldGen.DefaultSeed, blocks);

        ushort[]? level1, level2;
        using (var store = new WorldStore(path, generator, blocks))
        {
            level1 = FetchCells(store, 1, 0, 0, 0);
            Assert.NotNull(level1); // spawn area has terrain
            Assert.Equal(ExpectedLevel1(store, blocks, 0, 0, 0), level1);

            // Level 2 must equal the mip of its 8 level-1 children.
            var children = new ushort[]?[8];
            for (int dy = 0; dy < 2; dy++)
            for (int dz = 0; dz < 2; dz++)
            for (int dx = 0; dx < 2; dx++)
            {
                children[ChunkLod.ChildIndex(dx, dy, dz)] = FetchCells(store, 1, dx, dy, dz);
            }
            level2 = FetchCells(store, 2, 0, 0, 0);
            Assert.Equal(ChunkLod.MipSections(children, blocks.Opaque), level2);
        }

        // Reopen: blobs come from the lods table (memory cache is gone).
        using (var store = new WorldStore(path, generator, blocks))
        {
            Assert.Equal(level1, FetchCells(store, 1, 0, 0, 0));
            Assert.Equal(level2, FetchCells(store, 2, 0, 0, 0));
        }
    }

    [Fact]
    public void Edit_invalidates_ancestors_at_every_level()
    {
        string path = TempDb();
        var blocks = DataLoader.LoadRegistries(FindDataDir()).Blocks;
        var generator = new WorldGen(WorldGen.DefaultSeed, blocks);
        ushort stone = blocks.Resolve("stone");

        using var store = new WorldStore(path, generator, blocks);
        store.LoadLodBlob(1, 0, 1, 0); // prime the caches
        store.LoadLodBlob(2, 0, 0, 0);

        // Fill a world 2×2×2 region in open sky at (0..1, 60..61, 0..1):
        // chunk (0,3,0) → level-1 section (0,1,0) cell (0,14,0)
        //              → level-2 section (0,0,0) cell (0,15,0).
        for (int y = 60; y <= 61; y++)
        for (int z = 0; z <= 1; z++)
        for (int x = 0; x <= 1; x++)
        {
            store.SetBlock(x, y, z, stone);
        }

        ushort[]? level1 = FetchCells(store, 1, 0, 1, 0);
        Assert.NotNull(level1);
        Assert.Equal(stone, level1[ChunkData.Index(0, 14, 0)]);
        Assert.Equal(ExpectedLevel1(store, blocks, 0, 1, 0), level1);

        // The Voxy mip never erases a solid: the edit survives to level 2 too.
        ushort[]? level2 = FetchCells(store, 2, 0, 0, 0);
        Assert.NotNull(level2);
        Assert.Equal(stone, level2[ChunkData.Index(0, 15, 0)]);
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
