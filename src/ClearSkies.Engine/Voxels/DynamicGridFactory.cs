using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Support;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>Builds grid entities from descriptions. A construction helper for SpawnGridHandler, the only caller: to
/// create a grid, send a <c>Spawn&lt;GridDescription&gt;</c> command.</summary>
public static class DynamicGridFactory
{
    /// <summary>
    /// Creates a grid with entity ID <paramref name="id"/> from <paramref name="description"/>. Its chunks mesh,
    /// light and get a body once its presence layers are decided (ChunkMeshSystem, GpuLightSystem, PhysicsBodySystem).
    /// The grid's Transform (its block space) is the description's pose, so the grid stands exactly where it was
    /// described; PhysicsBodySystem then puts its body at the centre of mass within it.
    /// </summary>
    public static Entity Create(World world, EntityId id, GridDescription description)
    {
        var entity = world.CreateEntity();
        entity.Set(new DynamicGrid { Locked = description.Locked });
        entity.Set(ResistsAir.Unshaped()); // feels the wind; AirshipResistanceSystem works out its entries from its blocks
        var b = description.Body;
        entity.Set(new Transform { Position = PhysicsConv.ToSilk(b.Position), Rotation = PhysicsConv.ToSilk(b.Rotation), Scale = Vector3D<float>.One });
        entity.Set(id);
        var volume = new ChunkVolume(entity, world);
        entity.Set(new ChunkGrid() { Volume = volume });
        entity.Set<OwnPresence>();
        entity.Set<Supportable>();
        entity.Set(new InterpolatedTransform()); // moved by ticks, drawn between them (TickInterpolationSystem)
        foreach (var v in description.Voxels) volume.SetBlock(v.X, v.Y, v.Z, v.Id, v.Orientation);
        entity.Set(description.Controls);
        entity.Set(new BodyStateOverride { LinearVelocity = b.LinearVelocity, AngularVelocity = b.AngularVelocity });
        return entity;
    }

    /// <summary>A live grid's description: its blocks, controls, lock, pose and velocities (from its body if it
    /// has one, else its Transform).</summary>
    public static GridDescription Describe(Entity entity, PhysicsWorld physics)
    {
        var volume = entity.Get<ChunkGrid>().Volume;
        var d = new GridDescription
        {
            Voxels = GridSerializer.Voxels(volume),
            Locked = entity.Get<DynamicGrid>().Locked,
            Controls = entity.Has<ShipControls>() ? entity.Get<ShipControls>() : default,
        };
        if (entity.Has<PhysicsBodyComponent>())
        {
            ref readonly var pb = ref entity.Get<PhysicsBodyComponent>();
            var body = pb.Body;
            var (p, q) = physics.GetBodyPose(body);
            d.Body = new BodyState { Position = PhysicsConv.ToBepu(pb.EntityPosition(p, q)), Rotation = q,
                LinearVelocity = physics.GetBodyLinearVelocity(body), AngularVelocity = physics.GetBodyAngularVelocity(body) };
        }
        else
        {
            ref readonly var t = ref entity.Get<Transform>();
            d.Body = new BodyState { Position = PhysicsConv.ToBepu(t.Position), Rotation = PhysicsConv.ToBepu(t.Rotation) };
            if (entity.Has<BodyStateOverride>())
                (d.Body.LinearVelocity, d.Body.AngularVelocity) = (entity.Get<BodyStateOverride>().LinearVelocity, entity.Get<BodyStateOverride>().AngularVelocity);
        }

        return d;
    }
}

/// <summary>A body state to give a grid's body once it has one (or straight away if it does): the pose from its
/// Transform, and these velocities. Set when a grid is spawned from a description; PhysicsBodySystem
/// applies and removes it.</summary>
public struct BodyStateOverride
{
    public System.Numerics.Vector3 LinearVelocity;
    public System.Numerics.Vector3 AngularVelocity;
}
