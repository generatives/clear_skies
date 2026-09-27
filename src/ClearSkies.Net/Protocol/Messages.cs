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

/// <summary>Client → host, first thing: who's joining and whether their game matches.</summary>
public readonly record struct Hello(ushort Version, PlayerId Player, string Name, ulong GenerationChecksum)
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.Hello); w.WriteUInt16(Version); w.WriteGuid(Player.Value); w.WriteString(Name); w.WriteUInt64(GenerationChecksum); }
    public static Hello Read(ref NetReader r) => new(r.ReadUInt16(), new PlayerId(r.ReadGuid()), r.ReadString(), r.ReadUInt64());
}

/// <summary>Host → client: the client's peer ID and first block of network IDs, the world seed, the host's tick, and
/// where the player will spawn (their saved position, or the spawn point), so terrain can load there first.</summary>
public readonly record struct Welcome(PeerId Peer, uint IdFirst, uint IdCount, ulong Seed, uint HostTick, Vector3 Spawn)
{
    public void Write(NetWriter w)
    {
        w.WriteByte((byte)MessageKind.Welcome); w.WriteUInt32(Peer.Value); w.WriteUInt32(IdFirst); w.WriteUInt32(IdCount);
        w.WriteUInt64(Seed); w.WriteUInt32(HostTick); w.WriteVector3(Spawn);
    }
    public static Welcome Read(ref NetReader r) => new(new PeerId(r.ReadUInt32()), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt64(), r.ReadUInt32(), r.ReadVector3());
}

/// <summary>Either way: the connection is ending, and why.</summary>
public readonly record struct DisconnectMessage(string Reason)
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.Disconnect); w.WriteString(Reason); }
    public static DisconnectMessage Read(ref NetReader r) => new(r.ReadString());
}

/// <summary>Host → everyone: a player joined or left (for the UI; their entity comes and goes by spawn and despawn).</summary>
public readonly record struct PlayerNotice(bool Joined, PeerId Peer, string Name)
{
    public void Write(NetWriter w) { w.WriteByte((byte)(Joined ? MessageKind.PlayerJoined : MessageKind.PlayerLeft)); w.WriteUInt32(Peer.Value); w.WriteString(Name); }
    public static PlayerNotice Read(bool joined, ref NetReader r) => new(joined, new PeerId(r.ReadUInt32()), r.ReadString());
}

/// <summary>A command on its way to its authority (relayed by the host when that's another client).</summary>
public readonly record struct CommandHeader(PeerId To, PeerId From, ushort Handler, uint Seq)
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.Command); w.WriteUInt32(To.Value); w.WriteUInt32(From.Value); w.WriteUInt16(Handler); w.WriteUInt32(Seq); }
    public static CommandHeader Read(ref NetReader r) => new(new PeerId(r.ReadUInt32()), new PeerId(r.ReadUInt32()), r.ReadUInt16(), r.ReadUInt32());
}

/// <summary>An event (an accepted command) going to everyone who applies it.</summary>
public readonly record struct EventHeader(ushort Handler, EventMeta Meta)
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.Event); w.WriteUInt16(Handler); Meta.Write(w); }
    public static EventHeader Read(ref NetReader r) => new(r.ReadUInt16(), EventMeta.Read(ref r));
}

/// <summary>A command's authority turned it down.</summary>
public readonly record struct Rejection(PeerId To, PeerId Authority, uint Seq)
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.Rejection); w.WriteUInt32(To.Value); w.WriteUInt32(Authority.Value); w.WriteUInt32(Seq); }
    public static Rejection Read(ref NetReader r) => new(new PeerId(r.ReadUInt32()), new PeerId(r.ReadUInt32()), r.ReadUInt32());
}

/// <summary>Client → host every 250 ms, and the host's immediate answer: for measuring round trip and the host's tick.</summary>
public readonly record struct TimePing(double ClientTimeMs)
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.TimePing); w.WriteDouble(ClientTimeMs); }
    public static TimePing Read(ref NetReader r) => new(r.ReadDouble());
}

public readonly record struct TimePong(double ClientTimeMs, uint HostTick, float HostFraction)
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.TimePong); w.WriteDouble(ClientTimeMs); w.WriteUInt32(HostTick); w.WriteSingle(HostFraction); }
    public static TimePong Read(ref NetReader r) => new(r.ReadDouble(), r.ReadUInt32(), r.ReadSingle());
}

/// <summary>A block of network IDs for a client to hand out (it asks for another when it runs low).</summary>
public readonly record struct IdBlockMessage(uint First, uint Count)
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.IdBlock); w.WriteUInt32(First); w.WriteUInt32(Count); }
    public static IdBlockMessage Read(ref NetReader r) => new(r.ReadUInt32(), r.ReadUInt32());
}
