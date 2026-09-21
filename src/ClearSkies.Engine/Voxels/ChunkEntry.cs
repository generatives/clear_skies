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

    /// <summary>Packed opacity words (see <c>VolumeGpuResources.WordsPerChunk</c>), cached across GPU
    /// (re)uploads. Null means "recompute from <see cref="Data"/>" — set on creation and invalidated on every
    /// block edit (see <c>ChunkVolume.SetBlock</c>), but NOT just because the GPU volume was reallocated: a
    /// reallocation needs every loaded chunk's opacity re-transmitted into the fresh buffer, but the actual
    /// bits are unchanged for any chunk that wasn't edited, so re-uploading can skip straight to writing this
    /// cached array instead of redoing the block-by-block scan. Keeps a mass re-upload after a reallocation
    /// cheap enough to do synchronously (see GpuResidencySystem) instead of needing to spread it across
    /// frames — which otherwise leaves the Opacity buffer (sampled directly for AO every frame, independent
    /// of the light flood) reading as all-air and the whole reallocated area flashing to full brightness
    /// until the real data lands.</summary>
    public uint[]? PackedOpacityWords { get; set; }

    /// <summary>One bit per 8³ brick of this chunk (bit = bx + 4*(by + 4*bz)): brick contains any opaque
    /// voxel / any non-opaque voxel. Computed alongside <see cref="PackedOpacityWords"/>; used to build the
    /// ray-traced lighting pass's surface-brick work list.</summary>
    public ulong BrickSolidMask { get; set; }
    public ulong BrickAirMask   { get; set; }

    /// <summary>Set on creation and every block edit (or CPU light change); cleared after the volume flood re-runs.</summary>
    public bool NeedsFlood { get; set; } = true;

    public ChunkEntry(ChunkData data, Entity entity)
    {
        Data   = data;
        Entity = entity;
    }
}
