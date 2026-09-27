using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Commands;

/// <summary>A discrete change to the game, handled by a registered <see cref="CommandHandler{T}"/>. Plain data: every
/// field needed to apply it on any machine.</summary>
public interface ICommand
{
    /// <summary>What it changes. By default its owner decides the command (see <see cref="CommandHandler{T}.Authority"/>).</summary>
    EntityAddress Target { get; }
}

/// <summary>Where a command points: an entity by network ID (grids, players, the world volume), or a block in a volume
/// (levers, wheels and other block entities, which are rebuilt from voxels and have no ID of their own).</summary>
public readonly record struct EntityAddress(uint Entity, bool IsBlock, Vector3D<int> Block)
{
    public static EntityAddress Of(uint entity) => new(entity, false, default);
    public static EntityAddress OfBlock(uint volume, Vector3D<int> block) => new(volume, true, block);

    public void Write(NetWriter w)
    {
        w.WriteUInt32(Entity);
        w.WriteBool(IsBlock);
        if (IsBlock) { w.WriteInt32(Block.X); w.WriteInt32(Block.Y); w.WriteInt32(Block.Z); }
    }

    public static EntityAddress Read(ref NetReader r)
    {
        uint entity = r.ReadUInt32();
        return r.ReadBool() ? OfBlock(entity, new Vector3D<int>(r.ReadInt32(), r.ReadInt32(), r.ReadInt32())) : Of(entity);
    }

    public override string ToString() => IsBlock ? $"{Entity}@({Block.X},{Block.Y},{Block.Z})" : Entity.ToString();
}

public enum Verdict : byte
{
    Accept,
    Reject,
}

/// <summary>What a handler needs to decide who has the authority over a command.</summary>
public readonly struct AuthorityContext
{
    private readonly CommandSystem _system;
    internal AuthorityContext(CommandSystem system, PeerId sender)
    {
        _system = system;
        Sender = sender;
    }

    /// <summary>The peer sending the command.</summary>
    public PeerId Sender { get; }

    /// <summary>The owner of an entity; the host for anything unknown (not spawned yet, or already gone).</summary>
    public PeerId OwnerOf(EntityAddress address) => _system.OwnerOf(address.Entity);

    public PeerId Host => PeerId.Host;
}

/// <summary>Where a command is being decided: on its authority, for a command from <see cref="Sender"/>.</summary>
public readonly record struct CommandContext(PeerId Sender, uint Tick)
{
    /// <summary>Whether the command came from this machine.</summary>
    public bool FromLocal(Session session) => Sender == session.LocalPeer;
}

/// <summary>How an event is being applied.</summary>
public readonly record struct ApplyContext(PeerId Origin, bool IsAuthority, bool IsPrediction, uint Tick);

/// <summary>What travels with an event: who sent the command (and their number for it), who decided it, and the
/// authority's number for this event on its target, so receivers apply events in order and drop duplicates.</summary>
public readonly record struct EventMeta(PeerId Origin, uint OriginSeq, PeerId Authority, uint Target, uint EventNumber, uint Tick)
{
    public void Write(NetWriter w)
    {
        w.WriteUInt32(Origin.Value); w.WriteUInt32(OriginSeq); w.WriteUInt32(Authority.Value);
        w.WriteUInt32(Target); w.WriteUInt32(EventNumber); w.WriteUInt32(Tick);
    }

    public static EventMeta Read(ref NetReader r) =>
        new(new PeerId(r.ReadUInt32()), r.ReadUInt32(), new PeerId(r.ReadUInt32()), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());
}

/// <summary>Carries commands and events between machines; implemented by the network layer. With nobody connected
/// every authority is local and nothing is sent (<see cref="LocalCommandRouter"/>).</summary>
public interface ICommandRouter
{
    /// <summary>Sends a command to the peer with the authority over it.</summary>
    void SendCommand(PeerId authority, ushort handlerId, uint seq, ReadOnlySpan<byte> payload);

    /// <summary>Sends an accepted command, as an event, to every other machine that should apply it.</summary>
    void BroadcastEvent(ushort handlerId, in EventMeta meta, ReadOnlySpan<byte> payload);

    /// <summary>Tells a sender its command <paramref name="seq"/> was rejected.</summary>
    void SendRejection(PeerId to, uint seq);
}

/// <summary>The router for a session with nobody connected: there's never anyone to send to.</summary>
public sealed class LocalCommandRouter : ICommandRouter
{
    public void SendCommand(PeerId authority, ushort handlerId, uint seq, ReadOnlySpan<byte> payload)
        => throw new InvalidOperationException($"No route to {authority}: nobody is connected.");

    public void BroadcastEvent(ushort handlerId, in EventMeta meta, ReadOnlySpan<byte> payload) { }

    public void SendRejection(PeerId to, uint seq) { }
}
