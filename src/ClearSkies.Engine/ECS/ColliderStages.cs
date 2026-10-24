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

    /// <summary>The chunk's blocks, held for the job; nothing for one with none that collide (air, or only levers).</summary>
    public override object? Prepare(PipelineChunk record)
    {
        if (pipeline.EntryOf(record.Key) is not { } entry || !entry.Data.HasAnyColliding()) return null;
        entry.Data.Retain(); // see ChunkData.Release: an unloaded chunk's arrays wait for this job
        return entry.Data;
    }

    public override PhysicsWorld.StaticCompoundBuild Make(ChunkKey key, StageInputs inputs)
    {
        if (inputs.Prepared is not ChunkData data) return PhysicsWorld.StaticCompoundBuild.None;
        try
        {
            var boxes = _decomposers.Value!.Decompose(data, mergeBlockTypes: true);
            return boxes.Count > 0 ? PhysicsWorld.PrepareStaticCompound(boxes) : PhysicsWorld.StaticCompoundBuild.None;
        }
        finally { data.Unretain(); }
    }
}

/// <summary>Adds a chunk's collider to the physics world in place of its old one (main thread); the collider goes when
/// it's let go of (the chunk's out of range, or gone).</summary>
public sealed class ColliderStage(ChunkPipeline pipeline, PhysicsWorld physics, BoxesStage boxes) : FrameStage
{
    public override string Name => "Collider";
    public override Purpose Serves => Purpose.Collide;
    public override bool AppliesTo(PipelineGrid grid) => grid.Level == 0;

    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => new Pipeline.Input[] { new(boxes, Relation.Self) };

    private sealed record Collider(StaticHandle Handle);

    public override StageResult Run(PipelineChunk record, StageInputs inputs)
    {
        var build = inputs.Get<PhysicsWorld.StaticCompoundBuild>(boxes, record.Key)!;
        if (build.BoxCount == 0) return StageResult.Done(); // the old one goes
        var o = record.Key.Pos.WorldOrigin;
        return StageResult.Done(new Collider(physics.AddStaticCompound(build, new Vector3(o.X, o.Y, o.Z))));
    }

    public override void Release(object output) => physics.RemoveStaticCompound(((Collider)output).Handle);

    /// <summary>Whether <paramref name="volume"/>'s chunk at <paramref name="pos"/> has its collider in: is the ground
    /// under something actually collidable, as opposed to just loaded or drawn.</summary>
    public bool HasCollider(ChunkVolume volume, ChunkPosition pos)
        => volume.GetEntry(pos)?.Entity is { } e && e.Has<InPipeline>()
           && pipeline.Get(e.Get<InPipeline>().Key)?.Output<Collider>(this) != null;

    /// <summary>Whether every loaded chunk of <paramref name="volume"/> with colliding blocks within
    /// <paramref name="radius"/> of <paramref name="centre"/> has its collider, so a body placed there won't fall through.
    /// (A chunk of only passable blocks, e.g. levers, has blocks but never gets a collider.)</summary>
    public bool CollidersReady(ChunkVolume volume, Vector3 centre, float radius)
    {
        const int S = ChunkData.Size;
        int r = (int)MathF.Ceiling(radius / S);
        int cx = (int)MathF.Floor(centre.X / S), cy = (int)MathF.Floor(centre.Y / S), cz = (int)MathF.Floor(centre.Z / S);
        for (int dz = -r; dz <= r; dz++) for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++)
        {
            var pos = new ChunkPosition(cx + dx, cy + dy, cz + dz);
            if (volume.GetEntry(pos) is { } entry && entry.Data.HasAnyColliding() && !HasCollider(volume, pos)) return false;
        }
        return true;
    }
}
