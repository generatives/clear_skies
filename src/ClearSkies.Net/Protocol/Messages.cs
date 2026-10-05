using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Serialization;

namespace ClearSkies.Net.Protocol;

/// <summary>Bumped whenever any message or description format changes; a mismatch refuses the join.</summary>
public static class ProtocolVersion
{
    public const ushort Current = 4;
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
    PlayerInput = 20,
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

/// <summary>One tick of a player's input, numbered by the machine that plays them (see OwnPlayerPrediction).</summary>
/// <param name="Landed">What the input before this one left the player standing on, when it's something new: a ship
/// they landed on, as this machine saw it (see <see cref="Sync.RemoteInputs"/>). None otherwise.</param>
/// <param name="LandedAt">Where on <paramref name="Landed"/>, in its space.</param>
/// <param name="LandedVelocity">How fast they were moving then (world space).</param>
public readonly record struct InputSample(uint Sequence, PlayerButtons Held, PlayerButtons Pressed, float Yaw, float Pitch,
                                          EntityId Landed = default, Vector3 LandedAt = default, Vector3 LandedVelocity = default)
{
    public void Write(NetWriter w)
    {
        w.WriteUInt32(Sequence);
        w.WriteUInt32((uint)Held);
        w.WriteUInt32((uint)Pressed);
        w.WriteSingle(Yaw); // exactly: the host moves them the way they predicted they moved
        w.WriteSingle(Pitch);
        w.WriteBool(!Landed.IsNone);
        if (Landed.IsNone) return;
        Landed.Write(w);
        w.WriteSingle(LandedAt.X); w.WriteSingle(LandedAt.Y); w.WriteSingle(LandedAt.Z);
        w.WriteSingle(LandedVelocity.X); w.WriteSingle(LandedVelocity.Y); w.WriteSingle(LandedVelocity.Z);
    }

    public static InputSample Read(ref NetReader r)
    {
        var s = new InputSample(r.ReadUInt32(), (PlayerButtons)r.ReadUInt32(), (PlayerButtons)r.ReadUInt32(), r.ReadSingle(), r.ReadSingle());
        if (!r.ReadBool()) return s;
        return s with
        {
            Landed = EntityId.Read(ref r),
            LandedAt = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            LandedVelocity = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        };
    }

    public PlayerInput ToInput() => new() { Held = Held, Pressed = Pressed, Yaw = Yaw, Pitch = Pitch, Aiming = true };
}

/// <summary>Client → host, unreliable, every tick: the player's latest inputs, newest last. Each is sent in several of
/// these (<see cref="MaxSamples"/>), so a lost packet loses nothing; the host skips the ones it already has.</summary>
public readonly struct PlayerInputMessage : IMessage
{
    public const int MaxSamples = 4;
    public readonly InputSample[] Samples;
    public PlayerInputMessage(InputSample[] samples) => Samples = samples;

    public void Write(NetWriter w)
    {
        w.WriteByte((byte)MessageKind.PlayerInput);
        w.WriteByte((byte)Samples.Length);
        foreach (var s in Samples) s.Write(w);
    }

    public static PlayerInputMessage Read(ref NetReader r)
    {
        int count = System.Math.Min((int)r.ReadByte(), MaxSamples);
        var samples = new InputSample[count];
        for (int i = 0; i < count; i++) samples[i] = InputSample.Read(ref r);
        return new PlayerInputMessage(samples);
    }
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
