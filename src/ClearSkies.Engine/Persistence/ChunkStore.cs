using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Persistence;

/// <summary>Where edited terrain chunks are kept. The host's is the save database; a client (from N3) fetches them
/// from the host instead, and never reads or writes a save.</summary>
public interface IChunkStore
{
    /// <summary>Every chunk with saved data, read once at startup.</summary>
    IEnumerable<ChunkPosition> SavedChunks();

    /// <summary>Loads a saved chunk into <paramref name="data"/>; false if there's none. Called from worker threads.</summary>
    bool TryLoad(ChunkPosition pos, ChunkData data);

    void Save(ChunkPosition pos, ChunkData data);

    /// <summary>Whether a saved chunk can be loaded right now. A client's chunks come from the host, so one it hasn't
    /// received yet isn't ready: <see cref="Request"/> it and try again once it is.</summary>
    bool IsReady(ChunkPosition pos) => true;

    /// <summary>Asks for a chunk that isn't <see cref="IsReady"/> yet.</summary>
    void Request(ChunkPosition pos) { }
}

/// <summary>Edits terrain chunks that aren't loaded here (EditVoxels on the static world, far from this machine's
/// player).</summary>
public interface IWorldChunkEditor
{
    /// <summary>Applies <paramref name="edit"/> to the chunk at <paramref name="pos"/>, which isn't loaded.</summary>
    void EditUnloaded(ChunkPosition pos, Action<ChunkData> edit);

    /// <summary>Raised for each chunk edited while unloaded, so streaming knows it's been built on.</summary>
    event Action<ChunkPosition>? EditedUnloaded;
}

/// <summary>
/// The host's editor for unloaded terrain: the chunk comes from the save if it has been edited before, or else is
/// generated; the edit is applied and the chunk saved. Data only: no meshing, lighting or colliders. So an edit far
/// from the host is recorded, and whoever loads that chunk later (the host, or a client fetching it) sees it.
/// </summary>
public sealed class HostChunkEditor : IWorldChunkEditor
{
    private readonly IChunkStore _store;
    private readonly Func<Generation.IWorldGenerator> _generator;
    private Generation.IWorldGenerator? _cached;

    public HostChunkEditor(IChunkStore store, Func<Generation.IWorldGenerator> generator)
    {
        _store = store;
        _generator = generator;
    }

    public event Action<ChunkPosition>? EditedUnloaded;

    public void EditUnloaded(ChunkPosition pos, Action<ChunkData> edit)
    {
        var data = new ChunkData();
        if (!_store.TryLoad(pos, data)) (_cached ??= _generator()).Generate(data, pos);
        edit(data);
        _store.Save(pos, data);
        EditedUnloaded?.Invoke(pos);
    }
}

/// <summary>The host's chunk store: the save database's chunks table.</summary>
public sealed class DatabaseChunkStore : IChunkStore
{
    private readonly SaveDatabase _db;
    public DatabaseChunkStore(SaveDatabase db) => _db = db;

    public IEnumerable<ChunkPosition> SavedChunks() => _db.ChunkPositions();

    public bool TryLoad(ChunkPosition pos, ChunkData data)
    {
        if (_db.ReadChunk(pos) is not { } blob) return false;
        StaticWorldSerializer.Read(blob, data);
        return true;
    }

    public void Save(ChunkPosition pos, ChunkData data) => _db.WriteChunk(pos, StaticWorldSerializer.ToBytes(data));
}

/// <summary>A store that keeps nothing: nothing was ever saved, and saves are dropped.</summary>
public sealed class NoChunkStore : IChunkStore
{
    public IEnumerable<ChunkPosition> SavedChunks() => Array.Empty<ChunkPosition>();
    public bool TryLoad(ChunkPosition pos, ChunkData data) => false;
    public void Save(ChunkPosition pos, ChunkData data) { }
}
