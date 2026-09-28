using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.Commands;

/// <summary>Finds volumes and block entities from the addresses commands carry.</summary>
public sealed class BlockEntities
{
    private readonly EntityRegistry _registry;

    public BlockEntities(World world, EntityRegistry registry)
    {
        _registry = registry;
    }

    public EntityRegistry Registry => _registry;

    /// <summary>The volume with entity ID <paramref name="id"/>, if it's live here.</summary>
    public ChunkVolume? Volume(EntityId id) =>
        _registry.TryGet(id, out var e) && e.Has<ChunkGrid>() ? e.Get<ChunkGrid>().Volume : null;

    /// <summary>The block entity at a block address, if its volume and chunk are loaded here.</summary>
    public Entity? Find(in EntityAddress address)
    {
        if (!address.IsBlock || Volume(address.Entity) is not { } volume) return null;
        return volume.TryGetBlockEntity(address.Block.X, address.Block.Y, address.Block.Z, out var e) ? e : null;
    }

    /// <summary>The address of a block entity, if its volume has an entity ID.</summary>
    public static EntityAddress? AddressOf(Entity block)
    {
        if (!block.Has<BlockRef>()) return null;
        ref readonly var r = ref block.Get<BlockRef>();
        if (!r.Volume.Root.IsAlive || !r.Volume.Root.Has<EntityId>()) return null;
        return EntityAddress.OfBlock(r.Volume.Root.Get<EntityId>(), r.Position);
    }
}
