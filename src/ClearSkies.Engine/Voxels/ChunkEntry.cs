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

    /// <summary>Set when block occupancy changes and the physics collider must be rebuilt.</summary>
    public bool NeedsRecollide { get; set; } = true;

    /// <summary>Set on creation; cleared by <c>LightSystem</c> after initial CPU sky+block BFS.</summary>
    public bool NeedsRelight { get; set; } = true;

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
