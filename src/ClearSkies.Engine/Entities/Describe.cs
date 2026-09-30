using DefaultEcs;

namespace ClearSkies.Engine.Entities;

/// <summary>
/// One live entity's description, written out: what it is (a <see cref="GridDescription"/>, a
/// <see cref="PlayerDescription"/>), and its kind, the ID of the spawn handler that recreates it. Made by
/// <see cref="Commands.CommandSystem.Describe(IEnumerable{Entity})"/>. Who owns it is never part of it: whoever spawns it
/// from this decides that (a joining client gets it with its current owner; the save loads it with the default).
/// </summary>
public readonly record struct EntityDescription(Entity Entity, EntityId Id, ushort Kind, byte[] Data)
{
    /// <summary>A hash of the description, the same on every machine for the same state.</summary>
    public ulong Hash => DescriptionHash.Of(Data);
}

/// <summary>A spawn handler, seen without its description type: what describing and respawning need.</summary>
public interface ISpawnHandler
{
    /// <summary>Its command ID: the kind of the entities it describes.</summary>
    ushort Id { get; }

    /// <summary>Order among spawn handlers when describing: supports (grids) before what they support (players).</summary>
    int Order { get; }

    /// <summary>Whether <paramref name="entity"/> is its kind.</summary>
    bool Describes(Entity entity);

    /// <summary><paramref name="entity"/>'s description, written out.</summary>
    byte[] Describe(Entity entity);

    /// <summary>The spawn command that recreates an entity from its written-out description, written out.</summary>
    byte[] SpawnCommand(EntityId id, PeerId owner, ReadOnlySpan<byte> description);
}

/// <summary>FNV-1a, 64 bits: a stable hash of description bytes.</summary>
public static class DescriptionHash
{
    public static ulong Of(ReadOnlySpan<byte> data)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
