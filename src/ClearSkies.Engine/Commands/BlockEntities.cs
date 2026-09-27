using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.Commands;

/// <summary>Finds volumes and block entities from the addresses commands carry, and the groups of block entities that
/// move together (a volume's levers on one axis, a ship's wheels).</summary>
public sealed class BlockEntities
{
    private readonly NetRegistry _registry;
    private readonly EntitySet _levers;
    private readonly EntitySet _wheels;

    public BlockEntities(World world, NetRegistry registry)
    {
        _registry = registry;
        _levers = world.GetEntities().With<Lever>().With<BlockRef>().AsSet();
        _wheels = world.GetEntities().With<SteeringWheel>().With<BlockRef>().AsSet();
    }

    public NetRegistry Registry => _registry;

    /// <summary>The volume with network ID <paramref name="id"/>, if it's live here.</summary>
    public ChunkVolume? Volume(uint id) =>
        _registry.TryGet(id, out var e) && e.Has<ChunkGrid>() ? e.Get<ChunkGrid>().Volume : null;

    /// <summary>The block entity at a block address, if its volume and chunk are loaded here.</summary>
    public Entity? Find(in EntityAddress address)
    {
        if (!address.IsBlock || Volume(address.Entity) is not { } volume) return null;
        return volume.TryGetBlockEntity(address.Block.X, address.Block.Y, address.Block.Z, out var e) ? e : null;
    }

    /// <summary>The address of a block entity, if its volume has a network ID.</summary>
    public static EntityAddress? AddressOf(Entity block)
    {
        if (!block.Has<BlockRef>()) return null;
        ref readonly var r = ref block.Get<BlockRef>();
        if (!r.Volume.Root.IsAlive || !r.Volume.Root.Has<NetId>()) return null;
        return EntityAddress.OfBlock(r.Volume.Root.Get<NetId>().Value, r.Position);
    }

    /// <summary>The line a lever levers along in its volume (0-2: the north/south, east/west or up/down axis), and
    /// which way along it its north face points (+1 or -1): directions come in opposite pairs.</summary>
    public static (int Axis, float Sign) LeverAxis(in BlockRef block)
    {
        int north = (int)block.Orientation.North;
        return (north / 2, north % 2 == 0 ? 1f : -1f);
    }

    /// <summary>Every lever in <paramref name="lever"/>'s volume on the same axis, itself included, each with the value
    /// it takes when <paramref name="lever"/> is set to 1 (±1: levers facing the other way get the negated value, so all
    /// of them ask for the same thing).</summary>
    public IEnumerable<(Entity Lever, float Sign)> LeversOnSameAxis(Entity lever)
    {
        ref readonly var block = ref lever.Get<BlockRef>();
        var volume = block.Volume;
        var (axis, sign) = LeverAxis(block);
        var result = new List<(Entity, float)>();
        foreach (ref readonly Entity other in _levers.GetEntities())
        {
            ref readonly var otherBlock = ref other.Get<BlockRef>();
            if (otherBlock.Volume != volume) continue;
            var (otherAxis, otherSign) = LeverAxis(otherBlock);
            if (otherAxis == axis) result.Add((other, sign * otherSign));
        }
        return result;
    }

    /// <summary>Every wheel on <paramref name="wheel"/>'s ship, itself included.</summary>
    public IEnumerable<Entity> WheelsOnGrid(Entity wheel)
    {
        var volume = wheel.Get<BlockRef>().Volume;
        var result = new List<Entity>();
        foreach (ref readonly Entity other in _wheels.GetEntities())
            if (other.Get<BlockRef>().Volume == volume) result.Add(other);
        return result;
    }
}
