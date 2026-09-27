using ClearSkies.Engine.Commands;
using DefaultEcs;

namespace ClearSkies.Engine.Entities;

[Flags]
public enum DescribePurpose : byte
{
    None = 0,
    /// <summary>Send the description to <see cref="DescribeRequest.SendTo"/> as a spawn event (joining, resyncing).</summary>
    Send = 1,
    /// <summary>Write it to storage (unloading, autosave).</summary>
    Store = 2,
    /// <summary>Hash it for the divergence check.</summary>
    Hash = 4,
}

/// <summary>A set of peers (IDs 1-63), for who a description goes to.</summary>
public readonly record struct PeerSet(ulong Bits)
{
    public static readonly PeerSet Empty = new(0);
    public static PeerSet Of(PeerId peer) => new(Bit(peer));
    public PeerSet With(PeerId peer) => new(Bits | Bit(peer));
    public PeerSet Union(PeerSet other) => new(Bits | other.Bits);
    public bool Contains(PeerId peer) => (Bits & Bit(peer)) != 0;
    public bool IsEmpty => Bits == 0;

    public IEnumerable<PeerId> Peers
    {
        get
        {
            for (uint i = 1; i < 64; i++)
                if ((Bits & (1UL << (int)i)) != 0) yield return new PeerId(i);
        }
    }

    private static ulong Bit(PeerId peer) =>
        peer.Value is > 0 and < 64 ? 1UL << (int)peer.Value : throw new ArgumentOutOfRangeException(nameof(peer), $"{peer} can't be in a PeerSet.");
}

/// <summary>
/// Added to an entity to have it described this tick: turned back into its entity description (the spawn command
/// that would recreate it). The command system calls every describer once per tick, then removes all of these.
/// Add it with <see cref="Request"/>, which merges with a request already there.
/// </summary>
public struct DescribeRequest
{
    public DescribePurpose Purpose;

    /// <summary>For <see cref="DescribePurpose.Send"/>: the peers to send it to (a joining player, a resync requester).</summary>
    public PeerSet SendTo;

    public static void Request(Entity entity, DescribePurpose purpose, PeerSet sendTo = default)
    {
        if (entity.Has<DescribeRequest>())
        {
            ref var r = ref entity.Get<DescribeRequest>();
            r.Purpose |= purpose;
            r.SendTo = r.SendTo.Union(sendTo);
        }
        else entity.Set(new DescribeRequest { Purpose = purpose, SendTo = sendTo });
    }
}

/// <summary>A spawn handler that can describe its kind of entity.</summary>
public interface IDescriber
{
    /// <summary>Order among describers each tick: supports (grids) before what they support (players).</summary>
    int Order { get; }

    /// <summary>Once per tick: describe every entity of this kind that has a <see cref="DescribeRequest"/>.</summary>
    void Describe(DescriptionSink sink);
}

/// <summary>One entity's description: its spawn command, written out.</summary>
public readonly record struct Description(Entity Entity, uint NetId, DescribeRequest Request, ushort HandlerId, byte[] Payload,
                                          byte[]? HashPayload = null)
{
    /// <summary>A hash of the description's replicated state, the same on every machine for the same state: what the
    /// divergence check compares. It leaves out body state, which body sync only carries approximately.</summary>
    public ulong Hash => DescriptionHash.Of(HashPayload ?? Payload);
}

/// <summary>Where describers put their descriptions; hands each to whoever wants that purpose (the network for Send,
/// storage for Store, the divergence check for Hash), in describer order.</summary>
public sealed class DescriptionSink
{
    private readonly CommandSystem _commands;
    private readonly HashSet<Entity> _claimed = new();

    internal DescriptionSink(CommandSystem commands) => _commands = commands;

    /// <summary>Everything described this tick, in order.</summary>
    public event Action<Description>? Described;

    /// <param name="forHash">The same, less anything that isn't exactly replicated (body state), for the hash.</param>
    public void Add<T>(Entity entity, in T spawnCommand, T? forHash = null) where T : struct, ICommand
    {
        var handler = _commands.HandlerOf<T>();
        var request = entity.Has<DescribeRequest>() ? entity.Get<DescribeRequest>() : default;
        uint id = entity.Has<NetId>() ? entity.Get<NetId>().Value : 0;
        _claimed.Add(entity);
        byte[]? hashPayload = forHash is { } h ? handler.Serialize(h) : null;
        Described?.Invoke(new Description(entity, id, request, handler.Id, handler.Serialize(spawnCommand), hashPayload));
    }

    internal bool Claimed(Entity entity) => _claimed.Contains(entity);
    internal void Reset() => _claimed.Clear();
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
