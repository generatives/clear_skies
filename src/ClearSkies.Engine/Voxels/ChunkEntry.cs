using ClearSkies.Engine.Rendering;
using DefaultEcs;

namespace ClearSkies.Engine.Voxels;

internal sealed class ChunkEntry
{
    public ChunkData  Data        { get; }
    public Entity     Entity      { get; }
    public LightData  Light       { get; } = new();
    public GpuMesh?   Mesh        { get; set; }
    public bool       NeedsRemesh { get; set; } = true;

    /// <summary>
    /// Set when this chunk's block occupancy changes and its physics collider must be rebuilt.
    /// Unlike <see cref="NeedsRemesh"/>, this is NOT propagated to neighbours: a chunk's collision
    /// boxes depend only on its own blocks. Owned/consumed by <see cref="ECS.StaticColliderSystem"/>.
    /// </summary>
    public bool NeedsRecollide { get; set; } = true;

    /// <summary>Set on creation; cleared by <c>LightSystem</c> after initial sky+block BFS.</summary>
    public bool NeedsRelight { get; set; } = true;

    /// <summary>GPU-resident lighting buffers (opacity + light ping-pong). Created lazily by
    /// <c>GpuResidencySystem</c> for chunks with solid blocks; null until then / for empty chunks.</summary>
    public ChunkGpuResources? Gpu { get; set; }

    /// <summary>Set on creation and on every block edit; cleared once the GPU opacity buffer is re-uploaded.</summary>
    public bool NeedsGpuUpload { get; set; } = true;

    /// <summary>Set on creation and on every block edit; cleared once the GPU block-light flood re-runs.</summary>
    public bool NeedsFlood { get; set; } = true;

    public ChunkEntry(ChunkData data, Entity entity)
    {
        Data   = data;
        Entity = entity;
    }
}
