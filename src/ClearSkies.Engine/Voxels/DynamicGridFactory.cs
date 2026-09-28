using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Support;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>Builds grid entities from descriptions. A construction helper for SpawnGridHandler, the only caller: to
/// create a grid, send a SpawnGrid command.</summary>
public static class DynamicGridFactory
{
    /// <summary>
    /// Creates a grid with network ID <paramref name="netId"/> from <paramref name="description"/>. Its chunks mesh,
    /// light and get a body once its presence layers are decided (ChunkMeshSystem, GpuLightSystem, PhysicsBodySystem).
    /// The grid's Transform (its block space) is the description's pose, so the grid stands exactly where it was
    /// described; PhysicsBodySystem then puts its body at the centre of mass within it.
    /// </summary>
    public static Entity Create(World world, uint netId, GridDescription description)
    {
        var entity = world.CreateEntity();
        entity.Set(new DynamicGrid { Locked = description.Locked });
        entity.Set(Transform.Identity);
        entity.Set(new NetId { Value = netId });
        var volume = new ChunkVolume(entity, world);
        entity.Set(new ChunkGrid() { Volume = volume });
        entity.Set<OwnPresence>();
        entity.Set<Supportable>();
        Fill(entity, description);
        return entity;
    }

    /// <summary>Makes an existing grid match <paramref name="description"/>: its blocks, block entity state, lock and
    /// body state. Used to create a grid and to overwrite one in place (a spawn received twice, a resync).</summary>
    public static void Fill(Entity entity, GridDescription description)
    {
        var volume = entity.Get<ChunkGrid>().Volume;
        foreach (var pos in volume.All.Select(c => c.Key).ToList()) volume.RemoveChunk(pos);

        foreach (var v in description.Voxels) volume.SetBlock(v.X, v.Y, v.Z, v.Id, v.Orientation);
        foreach (var (cell, value) in description.Levers)
            if (volume.TryGetBlockEntity(cell.X, cell.Y, cell.Z, out var lever) && lever.Has<Lever>()) lever.Get<Lever>().Value = value;
        foreach (var (cell, angle) in description.Wheels)
            if (volume.TryGetBlockEntity(cell.X, cell.Y, cell.Z, out var wheel) && wheel.Has<SteeringWheel>()) wheel.Get<SteeringWheel>().Angle = angle;

        entity.Get<DynamicGrid>().Locked = description.Locked;
        var b = description.Body;
        ref var t = ref entity.Get<Transform>();
        t.Position = PhysicsConv.ToSilk(b.Position);
        t.Rotation = PhysicsConv.ToSilk(b.Rotation);
        entity.Set(new BodyStateOverride { LinearVelocity = b.LinearVelocity, AngularVelocity = b.AngularVelocity });
    }

    /// <summary>A live grid's description: its blocks, block entity state, lock, pose and velocities (from its body if it
    /// has one, else its Transform).</summary>
    public static GridDescription Describe(Entity entity, PhysicsWorld physics)
    {
        var volume = entity.Get<ChunkGrid>().Volume;
        var d = new GridDescription
        {
            Voxels = GridSerializer.Voxels(volume),
            Locked = entity.Get<DynamicGrid>().Locked,
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

        // Block entity state, in a fixed order.
        foreach (var (pos, entry) in volume.All.OrderBy(c => (c.Key.X, c.Key.Y, c.Key.Z)))
        {
            if (entry.BlockEntities is not { } entities) continue;
            foreach (var (cell, e) in entities.OrderBy(c => (c.Key.X, c.Key.Y, c.Key.Z)))
            {
                if (!e.IsAlive) continue;
                var p = new Vector3D<int>(pos.X, pos.Y, pos.Z) * ChunkData.Size + cell;
                if (e.Has<Lever>()) d.Levers.Add((p, e.Get<Lever>().Value));
                if (e.Has<SteeringWheel>()) d.Wheels.Add((p, e.Get<SteeringWheel>().Angle));
            }
        }
        return d;
    }
}

/// <summary>A body state to give a grid's body once it has one (or straight away if it does): the pose from its
/// Transform, and these velocities. Set when a grid is spawned or overwritten from a description; PhysicsBodySystem
/// applies and removes it.</summary>
public struct BodyStateOverride
{
    public System.Numerics.Vector3 LinearVelocity;
    public System.Numerics.Vector3 AngularVelocity;
}
