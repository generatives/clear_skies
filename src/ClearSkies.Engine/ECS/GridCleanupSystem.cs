using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

public class DynamicGridCleanupSystem : ISystem
{
    private readonly List<ChunkVolume> _removed = new();
    private readonly IDisposable _subscription;

    public DynamicGridCleanupSystem(World ecsWorld)
    {
        _subscription = ecsWorld.SubscribeEntityDisposed(OnEntityDisposed);
    }

    private void OnEntityDisposed(in Entity entity)
    {
        if (entity.Has<ChunkGrid>())
        {
            var gridComp = entity.Get<ChunkGrid>();
            _removed.Add(gridComp.Volume);
        }
    }

    public void Update(float dt)
    {
        foreach (var grid in _removed)
        {
            foreach (var (_, entry) in grid.All)
            {  
                if (entry.Entity.IsAlive) entry.Entity.Dispose();
            }
        }

        _removed.Clear();
    }
}