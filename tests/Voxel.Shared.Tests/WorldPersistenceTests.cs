using Microsoft.Data.Sqlite;
using Voxel.Server;
using Voxel.Shared;

namespace Voxel.Shared.Tests;

public class WorldPersistenceTests : IDisposable
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

    private string TempDb(string prefix) =>
        _tempPaths.Append(Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.db")).Last();
    private static WorldStore OpenTempStore(string path, BlockRegistry blocks)
    {
        var generator = new WorldGen(WorldGen.DefaultSeed, blocks);
        return new WorldStore(path, generator, blocks);
    }

    [Fact]
    public void Migration_v1_world_gains_entities_table()
    {
        string path = TempDb("voxel-migrate");
        var blocks = DataLoader.LoadRegistries(FindDataDir()).Blocks;
        var generator = new WorldGen(WorldGen.DefaultSeed, blocks);

        // Simulate a pre-M5 world: meta/chunks only, formatVersion = 1.
        using (var db = new SqliteConnection($"Data Source={path}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE chunks (
                  cx INTEGER NOT NULL, cy INTEGER NOT NULL, cz INTEGER NOT NULL,
                  blocks BLOB NOT NULL,
                  PRIMARY KEY (cx, cy, cz)
                ) WITHOUT ROWID;
                INSERT INTO meta (key, value) VALUES ('formatVersion', '1');
                INSERT INTO meta (key, value) VALUES ('generatorVersion', $gen);
                INSERT INTO meta (key, value) VALUES ('seed', $seed);
                INSERT INTO meta (key, value) VALUES ('palette', $palette);
                """;
            cmd.Parameters.AddWithValue("$gen", WorldGen.GeneratorVersion.ToString());
            cmd.Parameters.AddWithValue("$seed", generator.Seed.ToString());
            cmd.Parameters.AddWithValue("$palette", System.Text.Json.JsonSerializer.Serialize(blocks.Defs.Select(d => d.StringId)));
            cmd.ExecuteNonQuery();
        }

        using (var store = OpenTempStore(path, blocks))
        {
            Assert.Equal(2, int.Parse(store.GetMeta("formatVersion")!));
            Assert.Equal(0, store.EntityCount);
        }
    }

    [Fact]
    public void Entity_save_and_load_round_trips()
    {
        string path = TempDb("voxel-entity");
        var blocks = DataLoader.LoadRegistries(FindDataDir()).Blocks;
        using (var store = OpenTempStore(path, blocks))
        {
            ushort stone = blocks.Resolve("stone");
            var saved = new SavedEntity(
                7, 1, 2, 1, 2,
                [stone, stone, stone, stone],
                1f, 0.5f, 1f,
                10.5, 20.25, -3.5,
                0f, 0.1f, 0f, 0.995f,
                0.2f, -0.1f, 0f,
                0f, 0.5f, 0f,
                true);
            store.SaveEntity(saved);

            var loaded = store.LoadEntities();
            Assert.Single(loaded);
            Assert.Equivalent(saved, loaded[0]);
            Assert.Equal(1, store.EntityCount);
        }
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
