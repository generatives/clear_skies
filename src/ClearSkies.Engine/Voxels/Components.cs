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
public struct NeedsRecollideFlag { }