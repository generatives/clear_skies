using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Persistence;

/// <summary>Where edited terrain chunks come from (anything never edited is generated). The Host keeps them, and every
/// machine asks it (see ClearSkies.Net's HostChunkStore): streaming asks for an edited chunk as it's about to load it
/// (<see cref="Request"/>), waits until it's here (<see cref="IsReady"/>), and loads it once.</summary>
public interface IChunkStore
{
    /// <summary>Every chunk edited before this machine joined, read as streaming starts. Edits since reach streaming
    /// as they're made (a chunk edited here, or ECS.ChunkLoadSystem.EditedElsewhere).</summary>
    IEnumerable<ChunkPosition> EditedChunks();

    /// <summary>Whether an edited chunk's data is here to load; if not, <see cref="Request"/> it and try again.</summary>
    bool IsReady(ChunkPosition pos);

    /// <summary>Asks for an edited chunk that isn't <see cref="IsReady"/> yet.</summary>
    void Request(ChunkPosition pos);

    /// <summary>Loads an edited chunk into <paramref name="data"/> and lets go of it (asked for again if it loads again);
    /// false if there's none (generate it). Called from worker threads.</summary>
    bool TryLoad(ChunkPosition pos, ChunkData data);

    /// <summary>The chunk was edited since it was asked for: what's here of it is stale.</summary>
    void Outdated(ChunkPosition pos);
}
