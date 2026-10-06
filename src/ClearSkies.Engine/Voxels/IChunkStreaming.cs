namespace ClearSkies.Engine.Voxels;

/// <summary>
/// What streams a volume's chunks in and out (the static world's, see ECS.ChunkLoadSystem), as an edit to the volume
/// needs it: whether a chunk that isn't loaded is known to hold nothing (so an edit can make it), or still has content
/// on its way or not looked at (so an edit can't change it here, and what loads there later must have the edit).
/// </summary>
public interface IChunkStreaming
{
    /// <summary>Whether chunk <paramref name="pos"/>, not loaded, is known to hold nothing, with nothing on its way.</summary>
    bool IsKnownEmpty(ChunkPosition pos);

    /// <summary>An edit changed chunk <paramref name="pos"/> while it wasn't here (not loaded, and not known empty):
    /// what loads there from now on must have it.</summary>
    void EditedElsewhere(ChunkPosition pos);
}
