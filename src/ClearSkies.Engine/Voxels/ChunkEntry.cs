using ClearSkies.Engine.Rendering;
using DefaultEcs;

namespace ClearSkies.Engine.Voxels;

/// <summary>A light-emitting voxel within a chunk: chunk-local coordinates + emission level.</summary>
internal readonly record struct EmitterVoxel(byte Lx, byte Ly, byte Lz, byte Level);

internal sealed class ChunkEntry
{
    public ChunkData  Data        { get; }
    public Entity     Entity      { get; }
    public GpuMesh?   Mesh        { get; set; }
    public bool       NeedsRemesh { get; set; } = true;

    /// <summary>Light-emitting voxels in this chunk, rebuilt from <see cref="Data"/> on each opacity upload
    /// (see <c>VolumeGpuResources.UpdateChunkOpacity</c>). Gathered into the flood's scatter list each cycle.</summary>
    public List<EmitterVoxel> Emitters { get; } = new();

    /// <summary>Set when block occupancy changes and the physics collider must be rebuilt.</summary>
    public bool NeedsRecollide { get; set; } = true;

    /// <summary>Set on creation and every block edit; cleared after the volume opacity is re-uploaded to GPU.</summary>
    public bool NeedsGpuUpload { get; set; } = true;

    /// <summary>Set on creation and every block edit (or CPU light change); cleared after the volume flood re-runs.</summary>
    public bool NeedsFlood { get; set; } = true;

    public ChunkEntry(ChunkData data, Entity entity)
    {
        Data   = data;
        Entity = entity;
    }
}
