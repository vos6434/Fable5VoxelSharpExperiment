using Microsoft.Data.Sqlite;

namespace Voxel.Server;

/// <summary>
/// Ordered, transactional world-format migrations keyed off the meta
/// <c>formatVersion</c> value (plan 03 M5). v1→v2 adds the entities table;
/// v2→v3 adds the lods cache table (plan 04).
/// </summary>
internal static class WorldMigrations
{
    public const int CurrentFormatVersion = 3;

    public static void Run(SqliteConnection db)
    {
        int version = ReadFormatVersion(db);
        while (version < CurrentFormatVersion)
        {
            using var tx = db.BeginTransaction();
            try
            {
                Apply(db, version + 1);
                WriteFormatVersion(db, version + 1);
                tx.Commit();
                version++;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    private static int ReadFormatVersion(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'formatVersion'";
        var existing = cmd.ExecuteScalar() as string;
        return existing is null ? 1 : int.Parse(existing);
    }

    private static void WriteFormatVersion(SqliteConnection db, int version)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta (key, value) VALUES ('formatVersion', $value)
            ON CONFLICT(key) DO UPDATE SET value = $value
            """;
        cmd.Parameters.AddWithValue("$value", version.ToString());
        cmd.ExecuteNonQuery();
    }

    private static void Apply(SqliteConnection db, int targetVersion)
    {
        switch (targetVersion)
        {
            case 2:
                Execute(db, """
                    CREATE TABLE IF NOT EXISTS entities (
                      id INTEGER PRIMARY KEY,
                      kind INTEGER NOT NULL,
                      dim_x INTEGER NOT NULL,
                      dim_y INTEGER NOT NULL,
                      dim_z INTEGER NOT NULL,
                      blocks BLOB NOT NULL,
                      pivot_x REAL NOT NULL,
                      pivot_y REAL NOT NULL,
                      pivot_z REAL NOT NULL,
                      pos_x REAL NOT NULL,
                      pos_y REAL NOT NULL,
                      pos_z REAL NOT NULL,
                      quat_x REAL NOT NULL,
                      quat_y REAL NOT NULL,
                      quat_z REAL NOT NULL,
                      quat_w REAL NOT NULL,
                      vel_x REAL NOT NULL,
                      vel_y REAL NOT NULL,
                      vel_z REAL NOT NULL,
                      ang_vel_x REAL NOT NULL,
                      ang_vel_y REAL NOT NULL,
                      ang_vel_z REAL NOT NULL,
                      asleep INTEGER NOT NULL
                    ) WITHOUT ROWID;
                    """);
                break;
            case 3:
                // Downsampled-chunk cache (plan 04): deflated u16 LE cell
                // arrays; a zero-length blob caches "all air". Rows are
                // deleted when a source chunk is edited and rebuilt lazily.
                Execute(db, """
                    CREATE TABLE IF NOT EXISTS lods (
                      level INTEGER NOT NULL,
                      cx INTEGER NOT NULL, cy INTEGER NOT NULL, cz INTEGER NOT NULL,
                      blocks BLOB NOT NULL,
                      PRIMARY KEY (level, cx, cy, cz)
                    ) WITHOUT ROWID;
                    """);
                break;
            default:
                throw new InvalidOperationException($"unknown world format migration target v{targetVersion}");
        }
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
