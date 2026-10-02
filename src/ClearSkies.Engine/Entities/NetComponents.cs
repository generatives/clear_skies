using ClearSkies.Engine.Serialization;

namespace ClearSkies.Engine.Entities;

/// <summary>
/// Which entity this is, the same on every machine and across sessions: commands and events name entities by it, and
/// saves store them by it. Also the component that gives an entity its ID (players, grids and the world volume have
/// one); <see cref="EntityRegistry"/> looks entities up by it. <see cref="None"/> (0) is no entity.
/// </summary>
public readonly record struct EntityId(uint Value)
{
    public static readonly EntityId None = new(0);

    public bool IsNone => Value == 0;

    public void Write(NetWriter w) => w.WriteUInt32(Value);
    public static EntityId Read(ref NetReader r) => new(r.ReadUInt32());

    public override string ToString() => $"#{Value}";
}

/// <summary>Which peer owns (simulates, and decides commands for) an entity. <see cref="IsLocal"/> is whether that's
/// this machine: gameplay checks it, never the session role.</summary>
public struct NetOwner
{
    public PeerId Owner;
    public ushort Epoch;
    public bool IsLocal;
}

/// <summary>A player: who they are across sessions (<see cref="Id"/>), and who plays them. Every player is owned (simulated)
/// by the host (<see cref="NetOwner"/>); their <see cref="Controller"/> is the machine whose input drives them, which
/// predicts them too (see ClearSkies.Net's OwnPlayerPrediction).</summary>
public struct Player
{
    public PlayerId Id;
    public string Name;
    /// <summary>The machine whose input drives this player.</summary>
    public PeerId Controller;
    /// <summary>Whether they're played on this machine (<see cref="Controller"/> is this machine).</summary>
    public bool IsLocal;
}

/// <summary>Tag: the one player played on this machine. Input, the camera and the HUD follow it.</summary>
public struct LocalPlayer
{
}

/// <summary>A machine in the session. The host is always <see cref="Host"/>.</summary>
public readonly record struct PeerId(uint Value)
{
    public static readonly PeerId Host = new(1);
    public static readonly PeerId None = new(0);
    public override string ToString() => $"peer {Value}";
}

/// <summary>A player's identity, the same across sessions (names can clash). Generated once and kept in local
/// settings.</summary>
public readonly record struct PlayerId(Guid Value)
{
    public static PlayerId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N")[..8];
}
