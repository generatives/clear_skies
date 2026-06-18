using ClearSkies.Engine.Rendering;
using DefaultEcs;

namespace ClearSkies.Engine.Voxels;

internal sealed class ChunkEntry
{
    public ChunkData Data        { get; }
    public Entity    Entity      { get; }
    public GpuMesh?  Mesh        { get; set; }
    public bool      NeedsRemesh { get; set; } = true;

    public ChunkEntry(ChunkData data, Entity entity)
    {
        Data   = data;
        Entity = entity;
    }
}
