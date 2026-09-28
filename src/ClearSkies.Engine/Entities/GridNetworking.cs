using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.Entities;

/// <summary>
/// Makes every new dynamic grid a networked, supportable entity that decides its own presence: an entity ID, an owner
/// (this machine), <see cref="OwnPresence"/> and <see cref="Supportable"/>. Until spawning goes through spawn commands
/// (which assign these themselves), this covers every way a grid is created: G-spawn, loading a .grid file and the test
/// ship.
/// </summary>
public sealed class GridNetworking : IDisposable
{
    private readonly IDisposable _subscription;

    public GridNetworking(World world, EntityRegistry registry, Session session)
    {
        _subscription = world.SubscribeComponentAdded<DynamicGrid>((in Entity e, in DynamicGrid _) =>
        {
            if (!e.Has<EntityId>()) e.Set(registry.Allocate());
            if (!e.Has<NetOwner>()) e.Set(session.LocalOwner());
            e.Set<OwnPresence>();
            e.Set<Supportable>();
        });
    }

    public void Dispose() => _subscription.Dispose();
}
