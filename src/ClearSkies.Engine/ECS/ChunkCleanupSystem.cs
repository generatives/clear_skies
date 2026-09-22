using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

public class ChunkCleanupSystem : ISystem
{
    private readonly List<Entity> _removed = new();
    private readonly IDisposable _subscription;

    public ChunkCleanupSystem(World ecsWorld)
    {
        _subscription = ecsWorld.SubscribeEntityDisposed(OnEntityDisposed);
    }

    private void OnEntityDisposed(in Entity entity)
    {
        if (entity.Has<DynamicGridComponent>())
        {
            _removed.Add(entity);
        }
    }

    public void Update(float dt)
    {
        foreach (var entity in _removed)
        {
            var gridComp = entity.Get<DynamicGridComponent>();
            foreach (var (_, entry) in gridComp.Grid.All)
            {  
                if (entry.Entity.IsAlive) entry.Entity.Dispose();
            }
        }

        _removed.Clear();
    }
}