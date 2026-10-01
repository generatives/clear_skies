using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;

namespace ClearSkies.Net.Protocol;

/// <summary>Bumped whenever any message or description format changes; a mismatch refuses the join.</summary>
public static class ProtocolVersion
{
    public const ushort Current = 1;
}

/// <summary>The first byte of every packet.</summary>
public enum MessageKind : byte
{
    Hello = 1,
    Welcome = 2,
    Disconnect = 3,
    TerrainReady = 4,
    PlayerJoined = 5,
    PlayerLeft = 6,
    Command = 7,
    Event = 8,
    Rejection = 9,
    StateFrame = 10,
    TimePing = 11,
    TimePong = 12,
    EditedChunks = 13,
    ChunkRequest = 14,
    ChunkData = 15,
    StateHash = 16,
    SnapshotRequest = 17,
    IdBlockRequest = 18,
    IdBlock = 19,
}

/// <summary>A message: writes itself, kind byte first (see <see cref="Session.NetSession"/>'s Send).</summary>
public interface IMessage
{
    void Write(NetWriter w);
}

/// <summary>Client → host, first thing: who's joining (by name: the host gives each name its player ID) and whether
/// their game matches.</summary>
public readonly record struct Hello(ushort Version, string Name, ulong GenerationChecksum) : IMessage
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.Hello); w.WriteUInt16(Version); w.WriteString(Name); w.WriteUInt64(GenerationChecksum); }
    public static Hello Read(ref NetReader r) => new(r.ReadUInt16(), r.ReadString(), r.ReadUInt64());
}

/// <summary>Host → client: the client's peer ID and first block of entity IDs, the world seed, the host's tick, and
/// where the player will spawn (their saved position, or the spawn point), so terrain can load there first.</summary>
public readonly record struct Welcome(PeerId Peer, uint IdFirst, uint IdCount, ulong Seed, uint HostTick, Vector3 Spawn) : IMessage
{
    public void Write(NetWriter w)
    {
        w.WriteByte((byte)MessageKind.Welcome); w.WriteUInt32(Peer.Value); w.WriteUInt32(IdFirst); w.WriteUInt32(IdCount);
        w.WriteUInt64(Seed); w.WriteUInt32(HostTick); w.WriteVector3(Spawn);
    }
    public static Welcome Read(ref NetReader r) => new(new PeerId(r.ReadUInt32()), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt64(), r.ReadUInt32(), r.ReadVector3());
}

/// <summary>Client → host: the terrain around the spawn has loaded, so the world can be sent.</summary>
public readonly record struct TerrainReady : IMessage
{
    public void Write(NetWriter w) => w.WriteByte((byte)MessageKind.TerrainReady);
}

/// <summary>Client → host: its entity IDs are running low (answered with an <see cref="IdBlockMessage"/>).</summary>
public readonly record struct IdBlockRequest : IMessage
{
    public void Write(NetWriter w) => w.WriteByte((byte)MessageKind.IdBlockRequest);
}

/// <summary>Either way: the connection is ending, and why.</summary>
public readonly record struct DisconnectMessage(string Reason) : IMessage
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.Disconnect); w.WriteString(Reason); }
    public static DisconnectMessage Read(ref NetReader r) => new(r.ReadString());
}

/// <summary>Host → everyone: a player joined or left (for the UI; their entity comes and goes by spawn and despawn).</summary>
public readonly record struct PlayerNotice(bool Joined, PeerId Peer, string Name) : IMessage
{
    public void Write(NetWriter w) { w.WriteByte((byte)(Joined ? MessageKind.PlayerJoined : MessageKind.PlayerLeft)); w.WriteUInt32(Peer.Value); w.WriteString(Name); }
    public static PlayerNotice Read(bool joined, ref NetReader r) => new(joined, new PeerId(r.ReadUInt32()), r.ReadString());
}

/// <summary>A command on its way to its authority (relayed by the host when that's another client). The payload is the
/// command as its handler wrote it: only the handler reads it, so it's carried as bytes, and read from and written to
/// packets without a copy (a relay passes it straight on). A span, so this is a ref struct, which can't implement
/// <see cref="IMessage"/>: sessions send it with an overload of their own.</summary>
public readonly ref struct CommandMessage(PeerId to, PeerId from, ushort handler, uint seq, ReadOnlySpan<byte> payload)
{
    public readonly PeerId To = to;
    public readonly PeerId From = from;
    public readonly ushort Handler = handler;
    public readonly uint Seq = seq;
    public readonly ReadOnlySpan<byte> Payload = payload;

    public CommandMessage WithFrom(PeerId from) => new(To, from, Handler, Seq, Payload);

    public void Write(NetWriter w)
    {
        w.WriteByte((byte)MessageKind.Command); w.WriteUInt32(To.Value); w.WriteUInt32(From.Value); w.WriteUInt16(Handler); w.WriteUInt32(Seq);
        w.WriteRaw(Payload);
    }

    public static CommandMessage Read(ref NetReader r) =>
        new(new PeerId(r.ReadUInt32()), new PeerId(r.ReadUInt32()), r.ReadUInt16(), r.ReadUInt32(), r.ReadRaw(r.Remaining));
}

/// <summary>An event (an accepted command) going to everyone who applies it; its payload as in
/// <see cref="CommandMessage"/>.</summary>
public readonly ref struct EventMessage(ushort handler, EventMeta meta, ReadOnlySpan<byte> payload)
{
    public readonly ushort Handler = handler;
    public readonly EventMeta Meta = meta;
    public readonly ReadOnlySpan<byte> Payload = payload;

    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.Event); w.WriteUInt16(Handler); Meta.Write(w); w.WriteRaw(Payload); }

    public static EventMessage Read(ref NetReader r) => new(r.ReadUInt16(), EventMeta.Read(ref r), r.ReadRaw(r.Remaining));
}

/// <summary>A command's authority turned it down.</summary>
public readonly record struct Rejection(PeerId To, PeerId Authority, uint Seq) : IMessage
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.Rejection); w.WriteUInt32(To.Value); w.WriteUInt32(Authority.Value); w.WriteUInt32(Seq); }
    public static Rejection Read(ref NetReader r) => new(new PeerId(r.ReadUInt32()), new PeerId(r.ReadUInt32()), r.ReadUInt32());
}

/// <summary>Client → host every 250 ms, and the host's immediate answer: for measuring round trip and the host's tick.</summary>
public readonly record struct TimePing(double ClientTimeMs) : IMessage
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.TimePing); w.WriteDouble(ClientTimeMs); }
    public static TimePing Read(ref NetReader r) => new(r.ReadDouble());
}

public readonly record struct TimePong(double ClientTimeMs, uint HostTick, float HostFraction) : IMessage
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.TimePong); w.WriteDouble(ClientTimeMs); w.WriteUInt32(HostTick); w.WriteSingle(HostFraction); }
    public static TimePong Read(ref NetReader r) => new(r.ReadDouble(), r.ReadUInt32(), r.ReadSingle());
}

/// <summary>A block of entity IDs for a client to hand out (it asks for another when it runs low).</summary>
public readonly record struct IdBlockMessage(uint First, uint Count) : IMessage
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.IdBlock); w.WriteUInt32(First); w.WriteUInt32(Count); }
    public static IdBlockMessage Read(ref NetReader r) => new(r.ReadUInt32(), r.ReadUInt32());
}
