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
    ViewVolume = 21,
    Spawn = 22,
    Forget = 23,
    Release = 24,
    DescribeRequest = 25,
    Described = 26,
    Deleted = 27,
    SaveRequest = 28,
    SaveDone = 29,
}

/// <summary>A message: writes itself, kind byte first (see <see cref="Session.NetSession"/>'s Send).</summary>
public interface IMessage
{
    void Write(NetWriter w);
}

/// <summary>Participant → Host, first thing: who's joining (by name: the host gives each name its player ID) and whether
/// their game matches.</summary>
public readonly record struct Hello(ushort Version, string Name, ulong GenerationChecksum) : IMessage
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.Hello); w.WriteUInt16(Version); w.WriteString(Name); w.WriteUInt64(GenerationChecksum); }
    public static Hello Read(ref NetReader r) => new(r.ReadUInt16(), r.ReadString(), r.ReadUInt64());
}

/// <summary>Host → Participant: its peer ID (<see cref="PeerId.Host"/> for the hosting machine's, the authority) and first
/// block of entity IDs, the world seed, the Host's tick, and where its player will spawn (their saved position, or the
/// spawn point), for the camera to wait at.</summary>
public readonly record struct Welcome(PeerId Peer, uint IdFirst, uint IdCount, ulong Seed, uint HostTick, Vector3 Spawn) : IMessage
{
    public void Write(NetWriter w)
    {
        w.WriteByte((byte)MessageKind.Welcome); w.WriteUInt32(Peer.Value); w.WriteUInt32(IdFirst); w.WriteUInt32(IdCount);
        w.WriteUInt64(Seed); w.WriteUInt32(HostTick); w.WriteVector3(Spawn);
    }
    public static Welcome Read(ref NetReader r) => new(new PeerId(r.ReadUInt32()), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt64(), r.ReadUInt32(), r.ReadVector3());
}

/// <summary>One tick of a player's input, numbered by the machine that plays them (see OwnPlayerPrediction).</summary>
public readonly record struct InputSample(uint Sequence, PlayerButtons Held, PlayerButtons Pressed, float Yaw, float Pitch)
{
    public void Write(NetWriter w)
    {
        w.WriteUInt32(Sequence);
        w.WriteUInt32((uint)Held);
        w.WriteUInt32((uint)Pressed);
        w.WriteSingle(Yaw); // exactly: the host moves them the way they predicted they moved
        w.WriteSingle(Pitch);
    }

    public static InputSample Read(ref NetReader r) =>
        new(r.ReadUInt32(), (PlayerButtons)r.ReadUInt32(), (PlayerButtons)r.ReadUInt32(), r.ReadSingle(), r.ReadSingle());

    public PlayerInput ToInput() => new() { Held = Held, Pressed = Pressed, Yaw = Yaw, Pitch = Pitch, Aiming = true };
}

/// <summary>Participant → Host → the player's authority, unreliable, every tick: the player's latest inputs, newest
/// last. Each is sent in several of these (<see cref="MaxSamples"/>), so a lost packet loses nothing; the authority skips
/// the ones it already has. The Host passes it on only from the machine that plays <see cref="Player"/>.</summary>
public readonly struct PlayerInputMessage : IMessage
{
    public const int MaxSamples = 4;
    public readonly EntityId Player;
    public readonly InputSample[] Samples;

    public PlayerInputMessage(EntityId player, InputSample[] samples)
    {
        Player = player;
        Samples = samples;
    }

    public void Write(NetWriter w)
    {
        w.WriteByte((byte)MessageKind.PlayerInput);
        Player.Write(w);
        w.WriteByte((byte)Samples.Length);
        foreach (var s in Samples) s.Write(w);
    }

    public static PlayerInputMessage Read(ref NetReader r)
    {
        var player = EntityId.Read(ref r);
        int count = System.Math.Min((int)r.ReadByte(), MaxSamples);
        var samples = new InputSample[count];
        for (int i = 0; i < count; i++) samples[i] = InputSample.Read(ref r);
        return new PlayerInputMessage(player, samples);
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

/// <summary>Participant → Host, every half second: the space it wants to see (around its player, or where its player
/// will spawn). The Host streams it the entities inside.</summary>
public readonly record struct ViewVolumeMessage(Vector3 Centre, float Radius) : IMessage
{
    public void Write(NetWriter w) { w.WriteByte((byte)MessageKind.ViewVolume); w.WriteVector3(Centre); w.WriteSingle(Radius); }
    public static ViewVolumeMessage Read(ref NetReader r) => new(r.ReadVector3(), r.ReadSingle());
}

/// <summary>Host → Participant: an entity coming into its world, from its Description. <see cref="Owner"/> is who the
/// spawn says owns it (a player's: who plays them); <see cref="EventNumber"/> is its authority's last event the
/// Description includes, so later ones apply on top and earlier ones are skipped. <see cref="Position"/> is where it is in
/// the world, as the Host has it (for a player standing on a ship, the ship as the Host last heard).</summary>
public readonly ref struct SpawnMessage(EntityId id, ushort kind, PeerId owner, uint eventNumber, Vector3 position, ReadOnlySpan<byte> data)
{
    public readonly EntityId Id = id;
    public readonly ushort Kind = kind;
    public readonly PeerId Owner = owner;
    public readonly uint EventNumber = eventNumber;
    public readonly Vector3 Position = position;
    public readonly ReadOnlySpan<byte> Data = data;

    public void Write(NetWriter w)
    {
        w.WriteByte((byte)MessageKind.Spawn); Id.Write(w); w.WriteUInt16(Kind); w.WriteUInt32(Owner.Value); w.WriteUInt32(EventNumber);
        w.WriteVector3(Position); w.WriteRaw(Data);
    }

    public static SpawnMessage Read(ref NetReader r) =>
        new(EntityId.Read(ref r), r.ReadUInt16(), new PeerId(r.ReadUInt32()), r.ReadUInt32(), r.ReadVector3(), r.ReadRaw(r.Remaining));
}

/// <summary>An entity named by ID: Host → Participant <see cref="MessageKind.Forget"/> (out of your view: drop your
/// copy), Host → authority <see cref="MessageKind.Release"/> (out of every view: describe it, then despawn it) and
/// <see cref="MessageKind.DescribeRequest"/> (describe it now, for a Participant it's come into view of), authority →
/// Host <see cref="MessageKind.Deleted"/> (gone for good: out of the save too).</summary>
public readonly record struct EntityMessage(MessageKind Kind, EntityId Id) : IMessage
{
    public void Write(NetWriter w) { w.WriteByte((byte)Kind); Id.Write(w); }
    public static EntityMessage Read(MessageKind kind, ref NetReader r) => new(kind, EntityId.Read(ref r));
}

/// <summary>Why an authority describes an entity to the Host.</summary>
public enum DescribedReason : byte
{
    /// <summary>It made it (a ship built, say): the Host starts keeping it.</summary>
    Created,
    /// <summary>The Host asked (<see cref="MessageKind.DescribeRequest"/>), to send to a Participant.</summary>
    Requested,
    /// <summary>The Host released it: this is its last Description, and the authority has despawned it.</summary>
    Released,
    /// <summary>The Host is saving (<see cref="MessageKind.SaveRequest"/>).</summary>
    Save,
}

/// <summary>Authority → Host: an entity's Description. Its kind (the spawn handler that recreates it), its authority's
/// last event number, and where it is (none: a global entity, always loaded); the data is opaque to the Host, except a
/// player's, whose position on their ship it reads.</summary>
public readonly ref struct DescribedMessage(EntityId id, ushort kind, DescribedReason reason, uint eventNumber, Vector3? position,
                                            ReadOnlySpan<byte> data)
{
    public readonly EntityId Id = id;
    public readonly ushort Kind = kind;
    public readonly DescribedReason Reason = reason;
    public readonly uint EventNumber = eventNumber;
    public readonly Vector3? Position = position;
    public readonly ReadOnlySpan<byte> Data = data;

    public void Write(NetWriter w)
    {
        w.WriteByte((byte)MessageKind.Described); Id.Write(w); w.WriteUInt16(Kind); w.WriteByte((byte)Reason); w.WriteUInt32(EventNumber);
        w.WriteBool(Position.HasValue);
        if (Position is { } p) w.WriteVector3(p);
        w.WriteRaw(Data);
    }

    public static DescribedMessage Read(ref NetReader r)
    {
        var id = EntityId.Read(ref r);
        ushort kind = r.ReadUInt16();
        var reason = (DescribedReason)r.ReadByte();
        uint number = r.ReadUInt32();
        Vector3? position = r.ReadBool() ? r.ReadVector3() : null;
        return new(id, kind, reason, number, position, r.ReadRaw(r.Remaining));
    }
}

/// <summary>A message with nothing but its kind: Host → authority <see cref="MessageKind.SaveRequest"/> (describe
/// everything), authority → Host <see cref="MessageKind.SaveDone"/> (that's everything).</summary>
public readonly record struct SignalMessage(MessageKind Kind) : IMessage
{
    public void Write(NetWriter w) => w.WriteByte((byte)Kind);
}
