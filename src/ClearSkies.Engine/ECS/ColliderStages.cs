using System.Numerics;
using BepuPhysics;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Terrain chunks' static colliders, as pipeline stages after their blocks, serving <see cref="Purpose.Collide"/> (a
/// chunk near something that can touch it, see <see cref="EntityPresenceSystem"/>): its boxes and their tree are built on
/// a worker (<see cref="BoxesStage"/>), then added to the physics world (<see cref="ColliderStage"/>). The old collider
/// stays until the new one's in, so an edit never opens a hole for a frame. A ship's body is built whole, from all its
/// chunks (see <see cref="PhysicsBodySystem"/>).
/// </summary>
public static class ColliderStages
{
    /// <summary>Adds the stages to the pipeline (before it wants any chunk).</summary>
    public static ColliderStage Add(ChunkPipelineSystem system, PhysicsWorld physics)
    {
        var pipeline = system.Pipeline;
        var boxes = pipeline.Add(new BoxesStage(pipeline));
        return pipeline.Add(new ColliderStage(pipeline, physics, boxes));
    }
}

/// <summary>Splits a chunk's colliding blocks into boxes and builds their tree, on a worker (~0.2-0.35 ms a chunk, past
/// 2 ms on dense ones; see StreamingBenchmark). Terrain needs no mass, so a box may span block types: about half the
/// boxes.</summary>
public sealed class BoxesStage(ChunkPipeline pipeline) : WorkerStage<PhysicsWorld.StaticCompoundBuild>
{
    public override string Name => "Boxes";
    public override Purpose Serves => Purpose.Collide;
    public override bool AppliesTo(PipelineGrid grid) => grid.Level == 0; // nothing collides with coarse terrain

    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid)
        => IBlocksStage.Of(pipeline, grid).Select(blocks => new Pipeline.Input(blocks, Relation.Self)).ToArray();

    private readonly ThreadLocal<VoxelBoxDecomposer> _decomposers = new(() => new VoxelBoxDecomposer());

    /// <summary>The chunk's blocks, as they are now; nothing for one with none that collide (air, or only levers).</summary>
    public override object? Prepare(PipelineChunk record, StageInputs inputs)
        => inputs.Get<ChunkVoxels>(record.Key)?.Data is { } data && data.HasAnyColliding() ? data : null;

    public override PhysicsWorld.StaticCompoundBuild Make(ChunkKey key, StageInputs inputs)
    {
        if (inputs.Prepared is not ChunkData data) return PhysicsWorld.StaticCompoundBuild.None;
        var boxes = _decomposers.Value!.Decompose(data, mergeBlockTypes: true);
        return boxes.Count > 0 ? PhysicsWorld.PrepareStaticCompound(boxes) : PhysicsWorld.StaticCompoundBuild.None;
    }
}

/// <summary>A ship chunk's boxes, in its grid's block space, each of one block type with that block's weight (see
/// <see cref="ShipBoxesStage"/>): what its ship's body is built from (see <see cref="PhysicsBodySystem"/>).</summary>
public sealed record ShipBoxes(ChunkVolume Volume, (Vector3 Center, Vector3 Size, float Mass)[] Boxes);

/// <summary>Splits a ship chunk's colliding blocks into boxes of one block type each, with their mass, on a worker, before
/// anything else (it's what a ship's body waits on): an edit splits only the chunk it changed. Its ship's body is built
/// again as each comes, or goes (<paramref name="changed"/>).</summary>
public sealed class ShipBoxesStage(VolumeBlocksStage blocks, Action<ChunkVolume> changed)
    : WorkerStage<ShipBoxes>
{
    public override string Name => "Ship boxes";
    public override bool Urgent => true;
    public override bool AppliesTo(PipelineGrid grid) => blocks.AppliesTo(grid);
    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => new Pipeline.Input[] { new(blocks, Relation.Self) };

    private readonly ThreadLocal<VoxelBoxDecomposer> _decomposers = new(() => new VoxelBoxDecomposer());

    private sealed record Job(ChunkVolume Volume, ChunkData? Data);

    /// <summary>The chunk's ship, and its blocks as they are now.</summary>
    public override object? Prepare(PipelineChunk record, StageInputs inputs)
        => new Job(record.Grid.Volume!, inputs.Get<ChunkVoxels>(record.Key)?.Data);

    public override ShipBoxes Make(ChunkKey key, StageInputs inputs)
    {
        var (volume, data) = (Job)inputs.Prepared!;
        if (data == null || !data.HasAnyColliding()) return new ShipBoxes(volume, Array.Empty<(Vector3, Vector3, float)>());
        var o = key.Pos.WorldOrigin;
        var origin = new Vector3(o.X, o.Y, o.Z);
        return new ShipBoxes(volume, _decomposers.Value!.Decompose(data)
            .Select(b => (origin + b.center, b.size, b.size.X * b.size.Y * b.size.Z * BlockRegistry.Get(b.Id).Weight)).ToArray());
    }

    public override Change Applied(PipelineChunk record, ShipBoxes made, ShipBoxes? before)
    {
        changed(made.Volume);
        return Change.All;
    }

    public override void Release(object output) => changed(((ShipBoxes)output).Volume);
}

/// <summary>On a terrain chunk's entity while its static collider is in the physics world (see <see cref="ColliderStage"/>).</summary>
public struct StaticCollider
{
    public StaticHandle Handle;

    /// <summary>Whether every loaded chunk of <paramref name="volume"/> with colliding blocks within
    /// <paramref name="radius"/> of <paramref name="centre"/> has its collider, so a body placed there won't fall through.
    /// (A chunk of only passable blocks, e.g. levers, has blocks but never gets a collider.)</summary>
    public static bool ReadyAround(ChunkVolume volume, Vector3 centre, float radius)
    {
        const int S = ChunkData.Size;
        int r = (int)MathF.Ceiling(radius / S);
        int cx = (int)MathF.Floor(centre.X / S), cy = (int)MathF.Floor(centre.Y / S), cz = (int)MathF.Floor(centre.Z / S);
        for (int dz = -r; dz <= r; dz++) for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++)
        {
            if (volume.GetEntry(new ChunkPosition(cx + dx, cy + dy, cz + dz)) is { } entry && entry.Data.HasAnyColliding()
                && !entry.Entity.Has<StaticCollider>()) return false;
        }
        return true;
    }
}

/// <summary>Adds a chunk's collider to the physics world in place of its old one (main thread), and marks its entity
/// (<see cref="StaticCollider"/>); the collider goes when it's let go of (the chunk's out of range, or gone).</summary>
public sealed class ColliderStage(ChunkPipeline pipeline, PhysicsWorld physics, BoxesStage boxes) : FrameStage
{
    public override string Name => "Collider";
    public override Purpose Serves => Purpose.Collide;
    public override bool AppliesTo(PipelineGrid grid) => grid.Level == 0;

    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => new Pipeline.Input[] { new(boxes, Relation.Self) };

    private sealed record Collider(ChunkEntry Entry, StaticHandle Handle);

    public override StageResult Run(PipelineChunk record, StageInputs inputs)
    {
        var build = inputs.Get<PhysicsWorld.StaticCompoundBuild>(boxes, record.Key)!;
        if (build.BoxCount == 0 || pipeline.EntryOf(record.Key) is not { } entry) return StageResult.Done(); // the old one goes
        var o = record.Key.Pos.WorldOrigin;
        var handle = physics.AddStaticCompound(build, new Vector3(o.X, o.Y, o.Z));
        entry.Entity.Set(new StaticCollider { Handle = handle });
        return StageResult.Done(new Collider(entry, handle));
    }

    public override void Release(object output)
    {
        var (entry, handle) = (Collider)output;
        physics.RemoveStaticCompound(handle);
        if (entry.Entity is { IsAlive: true } e && e.Has<StaticCollider>() && e.Get<StaticCollider>().Handle.Equals(handle))
            e.Remove<StaticCollider>(); // not its replacement's
    }
}
