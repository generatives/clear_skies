using BepuPhysics;
using ClearSkies.Engine.ECS;
using DefaultEcs;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// A dynamic voxel grid: a <see cref="ChunkVolume"/> of blocks authored in the grid's own local space,
/// backed by a single BepuPhysics dynamic body. Blocks never move relative to each other — only the
/// body pose changes — which keeps the grid's internal representation motion-invariant (the property
/// Tier-2 lighting relies on). Editing a block grows chunks on demand and flags the collision shape
/// for rebuild.
/// </summary>
public sealed class DynamicGrid : ChunkVolume
{
    /// <summary>Root entity tagged with <see cref="DynamicGridComponent"/>; distinct from the per-chunk entities.</summary>
    public Entity Root { get; }

    /// <summary>World position at which the grid's centre of mass is placed when its body is first created.</summary>
    public PhysVec SpawnPosition { get; }

    public BodyHandle Body { get; internal set; }
    public bool       BodyCreated { get; internal set; }

    /// <summary>Centre of mass in grid-local space, updated on every shape rebuild. Render offsets subtract this.</summary>
    public PhysVec CenterOfMass { get; internal set; }

    /// <summary>Set when block occupancy changes; consumed by GridShapeSystem to rebuild the body shape + inertia.</summary>
    public bool ShapeDirty { get; internal set; } = true;

    public DynamicGrid(World world, PhysVec spawnPosition) : base(world)
    {
        SpawnPosition = spawnPosition;
        Root = world.CreateEntity();
        Root.Set(new DynamicGridComponent { Grid = this });
    }

    public override void SetBlock(int x, int y, int z, BlockId id)
    {
        var (cp, _, _, _) = Decompose(x, y, z);
        EnsureChunk(cp);          // grow on demand so edits outside existing chunks create new ones
        base.SetBlock(x, y, z, id);
        ShapeDirty = true;
    }

    protected override void PlaceChunkEntity(Entity entity, ChunkPosition pos)
    {
        // Initial local placement; GridTransformSystem overwrites the world pose each frame.
        var t = Transform.Identity;
        t.Position = pos.WorldOrigin;
        entity.Set(t);
    }
}
