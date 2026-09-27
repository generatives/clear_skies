namespace ClearSkies.Engine.Entities;

/// <summary>An entity's network ID: the same on every machine and across sessions. Looked up through
/// <see cref="NetRegistry"/>. Players, grids and the world volume have one.</summary>
public struct NetId
{
    public uint Value;
}

/// <summary>Which peer owns (simulates, and decides commands for) an entity. <see cref="IsLocal"/> is whether that's
/// this machine: gameplay checks it, never the session role.</summary>
public struct NetOwner
{
    public PeerId Owner;
    public ushort Epoch;
    public bool IsLocal;
}

/// <summary>A player: who they are across sessions (<see cref="Id"/>), and whether they play on this machine.</summary>
public struct Player
{
    public PlayerId Id;
    public string Name;
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
