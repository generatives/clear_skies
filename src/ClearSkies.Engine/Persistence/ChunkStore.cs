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

/// <summary>A client's store until N3 fetches chunks from the host: it has no save, so nothing was ever saved, and saves
/// are dropped.</summary>
public sealed class NoChunkStore : IChunkStore
{
    public IEnumerable<ChunkPosition> SavedChunks() => Array.Empty<ChunkPosition>();
    public bool TryLoad(ChunkPosition pos, ChunkData data) => false;
    public void Save(ChunkPosition pos, ChunkData data) { }
}
