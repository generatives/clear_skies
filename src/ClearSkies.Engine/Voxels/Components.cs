using ClearSkies.Engine.Voxels;

internal struct Chunk
{
    public ChunkEntry Entry;
}

public struct ChunkGrid
{
    public ChunkVolume Volume;
}

/// <summary>Flags a <see cref="ChunkGrid"/> as a low-resolution copy of the static world (its <see cref="ChunkVolume.Scale"/> is
/// above 1): meshed, lit and drawn like the world, but never targeted, edited, grappled or collided with. Systems that
/// act on blocks leave it out of their grids.</summary>
public struct LowResolutionCopy { }


/// <summary>Published (see <see cref="DefaultEcs.World.Publish{T}"/>) when an edit changes a chunk's blocks: the
/// faces whose border cells changed (bits 0-5, <see cref="ChunkVolume.FaceHasSolid"/>'s order), and whether the chunk
/// stopped or started being all opaque.</summary>
public readonly record struct ChunkBlocksChanged(DefaultEcs.Entity Chunk, int BorderFaces, bool OpacityChanged);

/// <summary>The chunk has been meshed since it started being drawn, so it's on screen (with its last mesh while a
/// remesh is pending). Removed when it stops being drawn. See <see cref="ECS.FogSystem"/>.</summary>
public struct ChunkMeshedFlag { }

/// <summary>The chunk has been uploaded to the GPU store since it started being drawn, so it's lit (with its last data
/// while a re-upload is pending). Removed when it stops being drawn. See <see cref="ECS.FogSystem"/>.</summary>
public struct ChunkUploadedFlag { }
public struct NeedsRecollideFlag { }