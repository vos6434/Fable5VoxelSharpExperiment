using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Voxel.Shared;

namespace Voxel.Server;

/// <summary>Another server process has the world open (see WorldStore's .lock guard).</summary>
public sealed class WorldLockedException(string worldPath)
    : IOException($"world \"{worldPath}\" is already in use by another server process");

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
    private readonly SqliteConnection _db;
    private readonly FileStream _lock;
    private readonly string _lockPath;
    private readonly WorldGen _generator;
    private readonly Dictionary<(int, int, int), ChunkData> _cache = new();
    /// <summary>Deflated LOD blobs by (level, sx, sy, sz); null = all air.</summary>
    private readonly Dictionary<(int, int, int, int), byte[]?> _lodCache = new();
    /// <summary>Registry opacity table (1 = hides faces); ranks blocks during LOD mips.</summary>
    private readonly byte[] _opaque;

    public WorldStore(string filePath, WorldGen generator, BlockRegistry blocks)
    {
        _generator = generator;
        _opaque = blocks.Opaque;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        // Single-writer guard: SQLite WAL would let two server processes open
        // the same world and diverge; make that a clean startup error instead.
        _lockPath = filePath + ".lock";
        try
        {
            _lock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new WorldLockedException(filePath);
        }

        _db = new SqliteConnection($"Data Source={filePath}");
        try
        {
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

            WorldMigrations.Run(_db);

            // LOD blobs derive from chunks + the vote algorithm; a rules change
            // makes every cached blob stale, so wipe and regenerate lazily.
            if (GetMeta("lodAlgoVersion") != ChunkLod.AlgoVersion.ToString())
            {
                Execute("DELETE FROM lods;");
                SetMeta("lodAlgoVersion", ChunkLod.AlgoVersion.ToString());
            }

            CheckMeta("generatorVersion", WorldGen.GeneratorVersion.ToString());
            CheckMeta("seed", generator.Seed.ToString());
            // Matches JSON.stringify(paletteArray) so worlds are portable across implementations.
            CheckMeta("palette", JsonSerializer.Serialize(blocks.Defs.Select(d => d.StringId)));
        }
        catch
        {
            _db.Dispose();
            _lock.Dispose();
            throw;
        }
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
        InvalidateLods(cx, cy, cz);
    }

    // ---- Section LOD pyramid (plan 04 v2) ----------------------------------------

    /// <summary>
    /// Deflated 16³-cell blob for a level-ℓ section (null = all air):
    /// memory → lods table → mip-from-level-below-and-persist. Level 1 mips
    /// from the 8 source chunks, higher levels from their 8 child sections,
    /// so a cold level-3 request builds (and caches) its whole subtree.
    /// </summary>
    public byte[]? LoadLodBlob(int level, int sx, int sy, int sz)
    {
        if (level is < 1 or > ChunkLod.MaxLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, $"LOD level must be 1..{ChunkLod.MaxLevel}");
        }

        var key = (level, sx, sy, sz);
        if (_lodCache.TryGetValue(key, out var cached)) return cached;

        byte[]? blob = ReadLodBlob(level, sx, sy, sz);
        if (blob is null)
        {
            var cells = BuildLodCells(level, sx, sy, sz);
            blob = cells is null ? [] : DeflateBlocks(cells);
            PersistLod(level, sx, sy, sz, blob);
        }
        byte[]? result = blob.Length == 0 ? null : blob;
        _lodCache[key] = result;
        return result;
    }

    private ushort[]? BuildLodCells(int level, int sx, int sy, int sz)
    {
        var children = new ushort[]?[8];
        for (int dy = 0; dy < 2; dy++)
        for (int dz = 0; dz < 2; dz++)
        for (int dx = 0; dx < 2; dx++)
        {
            int cx = sx * 2 + dx, cy = sy * 2 + dy, cz = sz * 2 + dz;
            if (level == 1)
            {
                // True mip from source chunks — the level-1 ring is near
                // enough that generating its chunks is affordable and edits
                // show faithfully.
                var chunk = Load(cx, cy, cz);
                children[ChunkLod.ChildIndex(dx, dy, dz)] = chunk.CountSolid() == 0 ? null : chunk.Blocks;
            }
            else
            {
                // Higher levels never touch the chunk generator (a level-3
                // section spans 512 chunks — recursing would generate the
                // world for minutes). Cached child sections are used where
                // they exist (explored terrain, faithful); everything else
                // comes from the terrain function directly (DH-style
                // distant generation).
                byte[]? childBlob = ReadLodBlob(level - 1, cx, cy, cz);
                children[ChunkLod.ChildIndex(dx, dy, dz)] = childBlob switch
                {
                    null => _generator.GenerateLodSection(level - 1, cx, cy, cz),
                    { Length: 0 } => null,
                    _ => InflateCells(childBlob),
                };
            }
        }
        return ChunkLod.MipSections(children, _opaque);
    }

    private static ushort[] InflateCells(byte[] blob)
    {
        byte[] raw = InflateRaw(blob);
        if (raw.Length != Constants.ChunkVolume * 2)
        {
            throw new InvalidDataException($"LOD blob: bad stored size {raw.Length}");
        }
        var cells = new ushort[Constants.ChunkVolume];
        Buffer.BlockCopy(raw, 0, cells, 0, raw.Length);
        return cells;
    }

    /// <summary>Drops the edited chunk's LOD ancestors (one section per level); they rebuild lazily.</summary>
    private void InvalidateLods(int cx, int cy, int cz)
    {
        for (int level = 1; level <= ChunkLod.MaxLevel; level++)
        {
            int sx = cx >> level, sy = cy >> level, sz = cz >> level;
            _lodCache.Remove((level, sx, sy, sz));
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM lods WHERE level = $level AND cx = $sx AND cy = $sy AND cz = $sz";
            cmd.Parameters.AddWithValue("$level", level);
            cmd.Parameters.AddWithValue("$sx", sx);
            cmd.Parameters.AddWithValue("$sy", sy);
            cmd.Parameters.AddWithValue("$sz", sz);
            cmd.ExecuteNonQuery();
        }
    }

    private void PersistLod(int level, int cx, int cy, int cz, byte[] blob)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO lods (level, cx, cy, cz, blocks) VALUES ($level, $cx, $cy, $cz, $blocks)";
        cmd.Parameters.AddWithValue("$level", level);
        cmd.Parameters.AddWithValue("$cx", cx);
        cmd.Parameters.AddWithValue("$cy", cy);
        cmd.Parameters.AddWithValue("$cz", cz);
        cmd.Parameters.AddWithValue("$blocks", blob);
        cmd.ExecuteNonQuery();
    }

    private byte[]? ReadLodBlob(int level, int cx, int cy, int cz)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT blocks FROM lods WHERE level = $level AND cx = $cx AND cy = $cy AND cz = $cz";
        cmd.Parameters.AddWithValue("$level", level);
        cmd.Parameters.AddWithValue("$cx", cx);
        cmd.Parameters.AddWithValue("$cy", cy);
        cmd.Parameters.AddWithValue("$cz", cz);
        return cmd.ExecuteScalar() as byte[];
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

    public int EntityCount
    {
        get
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM entities";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    // ---- Physics entities (plan 03 M5) ----------------------------------------

    public void SaveEntity(SavedEntity e)
    {
        byte[] raw = new byte[e.Blocks.Length * 2];
        Buffer.BlockCopy(e.Blocks, 0, raw, 0, raw.Length);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entities (
              id, kind, dim_x, dim_y, dim_z, blocks,
              pivot_x, pivot_y, pivot_z,
              pos_x, pos_y, pos_z,
              quat_x, quat_y, quat_z, quat_w,
              vel_x, vel_y, vel_z,
              ang_vel_x, ang_vel_y, ang_vel_z,
              asleep
            ) VALUES (
              $id, $kind, $dimX, $dimY, $dimZ, $blocks,
              $pivotX, $pivotY, $pivotZ,
              $posX, $posY, $posZ,
              $qx, $qy, $qz, $qw,
              $vx, $vy, $vz,
              $avx, $avy, $avz,
              $asleep
            )
            ON CONFLICT(id) DO UPDATE SET
              kind = $kind, dim_x = $dimX, dim_y = $dimY, dim_z = $dimZ, blocks = $blocks,
              pivot_x = $pivotX, pivot_y = $pivotY, pivot_z = $pivotZ,
              pos_x = $posX, pos_y = $posY, pos_z = $posZ,
              quat_x = $qx, quat_y = $qy, quat_z = $qz, quat_w = $qw,
              vel_x = $vx, vel_y = $vy, vel_z = $vz,
              ang_vel_x = $avx, ang_vel_y = $avy, ang_vel_z = $avz,
              asleep = $asleep
            """;
        cmd.Parameters.AddWithValue("$id", (long)e.Id);
        cmd.Parameters.AddWithValue("$kind", e.Kind);
        cmd.Parameters.AddWithValue("$dimX", e.DimX);
        cmd.Parameters.AddWithValue("$dimY", e.DimY);
        cmd.Parameters.AddWithValue("$dimZ", e.DimZ);
        cmd.Parameters.AddWithValue("$blocks", output.ToArray());
        cmd.Parameters.AddWithValue("$pivotX", e.PivotX);
        cmd.Parameters.AddWithValue("$pivotY", e.PivotY);
        cmd.Parameters.AddWithValue("$pivotZ", e.PivotZ);
        cmd.Parameters.AddWithValue("$posX", e.PosX);
        cmd.Parameters.AddWithValue("$posY", e.PosY);
        cmd.Parameters.AddWithValue("$posZ", e.PosZ);
        cmd.Parameters.AddWithValue("$qx", e.Qx);
        cmd.Parameters.AddWithValue("$qy", e.Qy);
        cmd.Parameters.AddWithValue("$qz", e.Qz);
        cmd.Parameters.AddWithValue("$qw", e.Qw);
        cmd.Parameters.AddWithValue("$vx", e.VelX);
        cmd.Parameters.AddWithValue("$vy", e.VelY);
        cmd.Parameters.AddWithValue("$vz", e.VelZ);
        cmd.Parameters.AddWithValue("$avx", e.AngVelX);
        cmd.Parameters.AddWithValue("$avy", e.AngVelY);
        cmd.Parameters.AddWithValue("$avz", e.AngVelZ);
        cmd.Parameters.AddWithValue("$asleep", e.Asleep ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void DeleteEntity(uint id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM entities WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", (long)id);
        cmd.ExecuteNonQuery();
    }

    public List<SavedEntity> LoadEntities()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, dim_x, dim_y, dim_z, blocks,
                   pivot_x, pivot_y, pivot_z,
                   pos_x, pos_y, pos_z,
                   quat_x, quat_y, quat_z, quat_w,
                   vel_x, vel_y, vel_z,
                   ang_vel_x, ang_vel_y, ang_vel_z,
                   asleep
            FROM entities ORDER BY id
            """;
        var list = new List<SavedEntity>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int volume = reader.GetInt32(2) * reader.GetInt32(3) * reader.GetInt32(4);
            byte[] raw = InflateRaw((byte[])reader.GetValue(5));
            if (raw.Length != volume * 2)
            {
                throw new InvalidDataException($"entity #{reader.GetInt64(0)}: bad blocks size {raw.Length}");
            }
            var blocks = new ushort[volume];
            Buffer.BlockCopy(raw, 0, blocks, 0, raw.Length);
            list.Add(new SavedEntity(
                (uint)reader.GetInt64(0),
                (byte)reader.GetInt32(1),
                (ushort)reader.GetInt32(2),
                (ushort)reader.GetInt32(3),
                (ushort)reader.GetInt32(4),
                blocks,
                (float)reader.GetDouble(6),
                (float)reader.GetDouble(7),
                (float)reader.GetDouble(8),
                reader.GetDouble(9),
                reader.GetDouble(10),
                reader.GetDouble(11),
                (float)reader.GetDouble(12),
                (float)reader.GetDouble(13),
                (float)reader.GetDouble(14),
                (float)reader.GetDouble(15),
                (float)reader.GetDouble(16),
                (float)reader.GetDouble(17),
                (float)reader.GetDouble(18),
                (float)reader.GetDouble(19),
                (float)reader.GetDouble(20),
                (float)reader.GetDouble(21),
                reader.GetInt32(22) != 0));
        }
        return list;
    }

    /// <summary>Raw-deflate compression of the chunk's u16 LE block array (zlib deflateRaw compatible).</summary>
    public static byte[] DeflateChunk(ChunkData chunk) => DeflateBlocks(chunk.Blocks);

    /// <summary>Raw-deflate compression of any u16 LE block/cell array (full chunks and LOD cells).</summary>
    public static byte[] DeflateBlocks(ushort[] blocks)
    {
        var raw = new byte[blocks.Length * 2];
        Buffer.BlockCopy(blocks, 0, raw, 0, raw.Length);
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

    public void Dispose()
    {
        _db.Dispose();
        _lock.Dispose();
        try { File.Delete(_lockPath); } catch { /* another process may have grabbed it already */ }
    }
}
