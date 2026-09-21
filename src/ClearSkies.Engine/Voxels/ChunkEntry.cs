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

    /// <summary>Light-emitting voxels in this chunk, rebuilt from <see cref="Data"/> whenever its opacity is
    /// repacked (see <c>GridStore.UploadChunk</c>). Gathered into the lamp list each frame.</summary>
    public List<EmitterVoxel> Emitters { get; } = new();

    /// <summary>Set when block occupancy changes and the physics collider must be rebuilt.</summary>
    public bool NeedsRecollide { get; set; } = true;

    /// <summary>Set on creation and every block edit; cleared after the volume opacity is re-uploaded to GPU.</summary>
    public bool NeedsGpuUpload { get; set; } = true;

    /// <summary>Packed opacity words (see <c>GridStore.WordsPerChunk</c>). Null means "recompute from
    /// <see cref="Data"/>" — set on creation and invalidated on every block edit (see
    /// <c>ChunkVolume.SetBlock</c>).</summary>
    public uint[]? PackedOpacityWords { get; set; }

    /// <summary>One bit per 8³ brick of this chunk (bit = bx + 4*(by + 4*bz)): brick contains any opaque
    /// voxel / any non-opaque voxel. Computed alongside <see cref="PackedOpacityWords"/>; decides which bricks
    /// get light storage.</summary>
    public ulong BrickSolidMask { get; set; }
    public ulong BrickAirMask   { get; set; }

    public ChunkEntry(ChunkData data, Entity entity)
    {
        Data   = data;
        Entity = entity;
    }
}
