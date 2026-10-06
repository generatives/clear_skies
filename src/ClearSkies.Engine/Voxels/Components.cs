using ClearSkies.Engine.Voxels;

internal struct Chunk
{
    public ChunkEntry Entry;
}

public struct ChunkGrid
{
    public ChunkVolume Volume;
}

public struct NeedsRemeshFlag { }
public struct NeedsGpuUploadFlag { }

/// <summary>The chunk has been meshed since it started being drawn, so it's on screen (with its last mesh while a
/// remesh is pending). Removed when it stops being drawn. See <see cref="ECS.FogSystem"/>.</summary>
public struct ChunkMeshedFlag { }

/// <summary>The chunk has been uploaded to the GPU store since it started being drawn, so it's lit (with its last data
/// while a re-upload is pending). Removed when it stops being drawn. See <see cref="ECS.FogSystem"/>.</summary>
public struct ChunkUploadedFlag { }
public struct NeedsRecollideFlag { }