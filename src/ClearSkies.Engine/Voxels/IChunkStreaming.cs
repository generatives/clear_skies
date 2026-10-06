namespace ClearSkies.Engine.Voxels;

/// <summary>What streaming (ECS.ChunkLoadSystem) knows of the static world's chunks that aren't loaded, for edits to
/// it: whether one is known to hold nothing, so an edit can make it here. Any other isn't here as it is (its content
/// hasn't loaded, or it hasn't been looked at), so an edit can't change it here.</summary>
public interface IChunkStreaming
{
    /// <summary>Whether chunk <paramref name="pos"/>, not loaded, is known to hold nothing, with nothing on its way.</summary>
    bool IsKnownEmpty(ChunkPosition pos);
}
