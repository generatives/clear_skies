using System.Globalization;
using System.Numerics;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Voxels;
using Microsoft.Data.Sqlite;

namespace ClearSkies.Engine.Persistence;

/// <summary>An entity's row in the index: which spawn command recreates it, and where it is (none for a global
/// entity, which is always loaded).</summary>
public readonly record struct StoredEntity(EntityId Id, ushort Kind, Vector3? Position);

/// <summary>
/// One world's save: a SQLite database (Saves/Worlds/&lt;name&gt;.db) holding the world's settings, every stored
/// entity's description, every player's description, and every edited terrain chunk. Only the host has one. Calls are
/// serialised with a lock (chunk loads come from worker threads), and <see cref="InTransaction"/> makes a batch of
/// writes atomic, so a crash mid-save leaves the previous save intact. Descriptions and the schema aren't versioned
/// yet: saves may be wiped when formats change during development.
/// </summary>
public sealed class SaveDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();
    private SqliteTransaction? _transaction;

    public SaveDatabase(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        Execute("PRAGMA journal_mode = WAL;");
        Execute("PRAGMA synchronous = NORMAL;");
        Execute(@"CREATE TABLE IF NOT EXISTS world (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                  CREATE TABLE IF NOT EXISTS entities (id INTEGER PRIMARY KEY, kind INTEGER NOT NULL, x REAL, y REAL, z REAL, data BLOB NOT NULL);
                  CREATE TABLE IF NOT EXISTS players (player_id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE, data BLOB);
                  CREATE TABLE IF NOT EXISTS chunks (x INTEGER NOT NULL, y INTEGER NOT NULL, z INTEGER NOT NULL, data BLOB NOT NULL, PRIMARY KEY (x, y, z));");
    }

    /// <summary>Opens (creating if needed) the world save at <paramref name="path"/>.</summary>
    public static SaveDatabase Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new SaveDatabase(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
    }

    /// <summary>A save that lives only in memory, for tests.</summary>
    public static SaveDatabase InMemory() => new("Data Source=:memory:");

    /// <summary>Where a world called <paramref name="name"/> is saved, next to Saves/Grids.</summary>
    public static string PathFor(string name) => Path.Combine(AppContext.BaseDirectory, "Saves", "Worlds", name + ".db");

    // ── world settings ───────────────────────────────────────────────────────

    public string? GetWorld(string key)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT value FROM world WHERE key = $k");
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetWorld(string key, string value)
    {
        lock (_lock)
        {
            using var cmd = Command("INSERT INTO world (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v");
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The world's seed, or null for a world not created yet.</summary>
    public ulong? Seed
    {
        get => GetWorld("seed") is { } s ? ulong.Parse(s, CultureInfo.InvariantCulture) : null;
        set => SetWorld("seed", value!.Value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The first entity ID not handed out yet (see <see cref="EntityIdAllocator"/>).</summary>
    public uint NextFreeId
    {
        get => GetWorld("next_free_id") is { } s ? uint.Parse(s, CultureInfo.InvariantCulture) : EntityRegistry.FirstFreeId;
        set => SetWorld("next_free_id", value.ToString(CultureInfo.InvariantCulture));
    }

    // ── entities ─────────────────────────────────────────────────────────────

    /// <summary>Every stored entity's ID, kind and position: the stored-entity index, read at startup.</summary>
    public List<StoredEntity> ReadEntityIndex()
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT id, kind, x, y, z FROM entities");
            using var r = cmd.ExecuteReader();
            var list = new List<StoredEntity>();
            while (r.Read())
            {
                Vector3? pos = r.IsDBNull(2) ? null : new Vector3(r.GetFloat(2), r.GetFloat(3), r.GetFloat(4));
                list.Add(new StoredEntity(new EntityId((uint)r.GetInt64(0)), (ushort)r.GetInt32(1), pos));
            }
            return list;
        }
    }

    public (ushort Kind, byte[] Data)? ReadEntity(EntityId id)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT kind, data FROM entities WHERE id = $id");
            cmd.Parameters.AddWithValue("$id", (long)id.Value);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ((ushort)r.GetInt32(0), (byte[])r["data"]) : null;
        }
    }

    /// <summary>Writes an entity's description; <paramref name="position"/> null for a global entity.</summary>
    public void WriteEntity(EntityId id, ushort kind, Vector3? position, byte[] data)
    {
        lock (_lock)
        {
            using var cmd = Command(@"INSERT INTO entities (id, kind, x, y, z, data) VALUES ($id, $kind, $x, $y, $z, $data)
                                      ON CONFLICT(id) DO UPDATE SET kind = $kind, x = $x, y = $y, z = $z, data = $data");
            cmd.Parameters.AddWithValue("$id", (long)id.Value);
            cmd.Parameters.AddWithValue("$kind", (int)kind);
            cmd.Parameters.AddWithValue("$x", position is { } p ? p.X : DBNull.Value);
            cmd.Parameters.AddWithValue("$y", position is { } q ? q.Y : DBNull.Value);
            cmd.Parameters.AddWithValue("$z", position is { } w ? w.Z : DBNull.Value);
            cmd.Parameters.AddWithValue("$data", data);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteEntity(EntityId id)
    {
        lock (_lock)
        {
            using var cmd = Command("DELETE FROM entities WHERE id = $id");
            cmd.Parameters.AddWithValue("$id", (long)id.Value);
            cmd.ExecuteNonQuery();
        }
    }

    // ── players ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The player called <paramref name="name"/> in this world: players are known by name, and the host gives each name
    /// a player ID the first time it sees it. The ID then keys the player's saved state.
    /// </summary>
    public PlayerId PlayerFor(string name)
    {
        lock (_lock)
        {
            using (var find = Command("SELECT player_id FROM players WHERE name = $n"))
            {
                find.Parameters.AddWithValue("$n", name);
                if (find.ExecuteScalar() is string id) return new PlayerId(Guid.Parse(id));
            }
            var player = PlayerId.New();
            using var add = Command("INSERT INTO players (player_id, name) VALUES ($p, $n)");
            add.Parameters.AddWithValue("$p", player.Value.ToString("N"));
            add.Parameters.AddWithValue("$n", name);
            add.ExecuteNonQuery();
            return player;
        }
    }

    /// <summary>The player's saved spawn; null if they haven't been saved in this world yet.</summary>
    public byte[]? ReadPlayer(PlayerId player)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT data FROM players WHERE player_id = $p");
            cmd.Parameters.AddWithValue("$p", player.Value.ToString("N"));
            return cmd.ExecuteScalar() as byte[];
        }
    }

    public void WritePlayer(PlayerId player, string name, byte[] data)
    {
        lock (_lock)
        {
            using var cmd = Command(@"INSERT INTO players (player_id, name, data) VALUES ($p, $n, $d)
                                      ON CONFLICT(player_id) DO UPDATE SET data = $d");
            cmd.Parameters.AddWithValue("$p", player.Value.ToString("N"));
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$d", data);
            cmd.ExecuteNonQuery();
        }
    }

    public int PlayerCount
    {
        get { lock (_lock) { using var cmd = Command("SELECT COUNT(*) FROM players"); return Convert.ToInt32(cmd.ExecuteScalar()); } }
    }

    // ── chunks ───────────────────────────────────────────────────────────────

    public List<ChunkPosition> ChunkPositions()
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT x, y, z FROM chunks");
            using var r = cmd.ExecuteReader();
            var list = new List<ChunkPosition>();
            while (r.Read()) list.Add(new ChunkPosition(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
            return list;
        }
    }

    public byte[]? ReadChunk(ChunkPosition pos)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT data FROM chunks WHERE x = $x AND y = $y AND z = $z");
            AddPosition(cmd, pos);
            return cmd.ExecuteScalar() as byte[];
        }
    }

    public void WriteChunk(ChunkPosition pos, byte[] data)
    {
        lock (_lock)
        {
            using var cmd = Command(@"INSERT INTO chunks (x, y, z, data) VALUES ($x, $y, $z, $d)
                                      ON CONFLICT(x, y, z) DO UPDATE SET data = $d");
            AddPosition(cmd, pos);
            cmd.Parameters.AddWithValue("$d", data);
            cmd.ExecuteNonQuery();
        }
    }

    private static void AddPosition(SqliteCommand cmd, ChunkPosition pos)
    {
        cmd.Parameters.AddWithValue("$x", pos.X);
        cmd.Parameters.AddWithValue("$y", pos.Y);
        cmd.Parameters.AddWithValue("$z", pos.Z);
    }

    // ── transactions ─────────────────────────────────────────────────────────

    /// <summary>Runs <paramref name="writes"/> as one transaction: all of it is saved, or (on an exception or a crash)
    /// none of it. Nested calls join the outer transaction.</summary>
    public void InTransaction(Action writes)
    {
        lock (_lock)
        {
            if (_transaction != null) { writes(); return; }
            _transaction = _connection.BeginTransaction();
            try
            {
                writes();
                _transaction.Commit();
            }
            catch
            {
                _transaction.Rollback();
                throw;
            }
            finally
            {
                _transaction.Dispose();
                _transaction = null;
            }
        }
    }

    private SqliteCommand Command(string sql)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _transaction;
        return cmd;
    }

    private void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_lock) _connection.Dispose();
    }
}
