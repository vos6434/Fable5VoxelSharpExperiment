using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Voxel.Shared;

namespace Voxel.Server;

/// <summary>
/// SQLite-backed chunk persistence, format-compatible with the web version:
/// WAL mode, chunks keyed (cx, cy, cz) with raw-deflate-compressed u16 LE
/// block arrays, and a meta table pinning format version, generator version,
/// seed, and the block palette. A world whose stored meta disagrees with the
/// loaded /data registries fails loudly instead of silently corrupting ids.
///
/// Not thread-safe by itself; GameServer serializes access with a lock.
/// </summary>
public sealed class WorldStore : IDisposable
{
    private const int FormatVersion = 1;

    private readonly SqliteConnection _db;
    private readonly WorldGen _generator;
    private readonly Dictionary<(int, int, int), ChunkData> _cache = new();

    public WorldStore(string filePath, WorldGen generator, BlockRegistry blocks)
    {
        _generator = generator;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _db = new SqliteConnection($"Data Source={filePath}");
        _db.Open();

        Execute("PRAGMA journal_mode = WAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS chunks (
              cx INTEGER NOT NULL, cy INTEGER NOT NULL, cz INTEGER NOT NULL,
              blocks BLOB NOT NULL,
              PRIMARY KEY (cx, cy, cz)
            ) WITHOUT ROWID;
            """);

        CheckMeta("formatVersion", FormatVersion.ToString());
        CheckMeta("generatorVersion", WorldGen.GeneratorVersion.ToString());
        CheckMeta("seed", generator.Seed.ToString());
        // Matches JSON.stringify(paletteArray) so worlds are portable across implementations.
        CheckMeta("palette", JsonSerializer.Serialize(blocks.Defs.Select(d => d.StringId)));
    }

    /// <summary>Free-form meta read (unlike CheckMeta, no pinning semantics).</summary>
    public string? GetMeta(string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Free-form meta upsert (used for mutable values like the world clock).</summary>
    public void SetMeta(string key, string value)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) " +
                          "ON CONFLICT(key) DO UPDATE SET value = $value";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Stores the value on first open; on later opens, requires an exact match.</summary>
    private void CheckMeta(string key, string value)
    {
        using var get = _db.CreateCommand();
        get.CommandText = "SELECT value FROM meta WHERE key = $key";
        get.Parameters.AddWithValue("$key", key);
        var existing = get.ExecuteScalar() as string;

        if (existing is null)
        {
            using var insert = _db.CreateCommand();
            insert.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value)";
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$value", value);
            insert.ExecuteNonQuery();
        }
        else if (existing != value)
        {
            throw new InvalidOperationException(
                $"world meta mismatch for \"{key}\": stored {existing}, current {value}. " +
                "Refusing to open (palette/seed migration is not implemented yet).");
        }
    }

    /// <summary>Cache → disk → generate-and-persist.</summary>
    public ChunkData Load(int cx, int cy, int cz)
    {
        if (_cache.TryGetValue((cx, cy, cz), out var cached)) return cached;

        ChunkData chunk;
        byte[]? blob = ReadBlob(cx, cy, cz);
        if (blob is not null)
        {
            byte[] raw = InflateRaw(blob);
            if (raw.Length != Constants.ChunkVolume * 2)
            {
                throw new InvalidDataException($"chunk {cx},{cy},{cz}: bad stored size {raw.Length}");
            }
            var blocks = new ushort[Constants.ChunkVolume];
            Buffer.BlockCopy(raw, 0, blocks, 0, raw.Length);
            chunk = new ChunkData(blocks);
        }
        else
        {
            chunk = _generator.Generate(cx, cy, cz).Chunk;
            Persist(cx, cy, cz, chunk);
        }
        _cache[(cx, cy, cz)] = chunk;
        return chunk;
    }

    /// <summary>Applies one block edit (world coordinates) and persists the chunk.</summary>
    public void SetBlock(int wx, int wy, int wz, ushort blockId)
    {
        int cx = Coords.WorldToChunk(wx);
        int cy = Coords.WorldToChunk(wy);
        int cz = Coords.WorldToChunk(wz);
        var chunk = Load(cx, cy, cz);
        chunk.Set(Coords.WorldToLocal(wx), Coords.WorldToLocal(wy), Coords.WorldToLocal(wz), blockId);
        Persist(cx, cy, cz, chunk);
    }

    public int ChunkCount
    {
        get
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM chunks";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Raw-deflate compression of the chunk's u16 LE block array (zlib deflateRaw compatible).</summary>
    public static byte[] DeflateChunk(ChunkData chunk)
    {
        var raw = new byte[Constants.ChunkVolume * 2];
        Buffer.BlockCopy(chunk.Blocks, 0, raw, 0, raw.Length);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw);
        }
        return output.ToArray();
    }

    private void Persist(int cx, int cy, int cz, ChunkData chunk)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO chunks (cx, cy, cz, blocks) VALUES ($cx, $cy, $cz, $blocks)";
        cmd.Parameters.AddWithValue("$cx", cx);
        cmd.Parameters.AddWithValue("$cy", cy);
        cmd.Parameters.AddWithValue("$cz", cz);
        cmd.Parameters.AddWithValue("$blocks", DeflateChunk(chunk));
        cmd.ExecuteNonQuery();
    }

    private byte[]? ReadBlob(int cx, int cy, int cz)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT blocks FROM chunks WHERE cx = $cx AND cy = $cy AND cz = $cz";
        cmd.Parameters.AddWithValue("$cx", cx);
        cmd.Parameters.AddWithValue("$cy", cy);
        cmd.Parameters.AddWithValue("$cz", cz);
        return cmd.ExecuteScalar() as byte[];
    }

    private static byte[] InflateRaw(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
