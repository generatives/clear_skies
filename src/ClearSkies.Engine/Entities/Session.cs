namespace ClearSkies.Engine.Entities;

public enum SessionRole
{
    /// <summary>Owns the world: the save, streaming entities, and deciding everything nobody else owns.
    /// Single-player is a host with nobody connected.</summary>
    Host,
    Client,
}

/// <summary>This machine's place in the session. Single-player is a <see cref="SessionRole.Host"/> session with the
/// transport off. The role decides which systems are set up; gameplay checks <see cref="NetOwner.IsLocal"/> instead.</summary>
public sealed class Session
{
    public Session(SessionRole role, PeerId localPeer)
    {
        Role = role;
        LocalPeer = localPeer;
    }

    /// <summary>A host session with nobody connected: single-player.</summary>
    public static Session SinglePlayer() => new(SessionRole.Host, PeerId.Host);

    public SessionRole Role { get; private set; }
    public PeerId LocalPeer { get; private set; }
    public bool IsHost => Role == SessionRole.Host;

    /// <summary>An owner record for something this machine owns.</summary>
    public NetOwner LocalOwner(ushort epoch = 0) => new() { Owner = LocalPeer, Epoch = epoch, IsLocal = true };

    /// <summary>An owner record for <paramref name="owner"/>, local or not.</summary>
    public NetOwner OwnerFor(PeerId owner, ushort epoch = 0) => new() { Owner = owner, Epoch = epoch, IsLocal = owner == LocalPeer };

    /// <summary>Each player's bubble owner (the peer that simulates their surroundings and decides their terrain
    /// edits), as the host's bubble manager last announced it. Anyone missing is in the host's.</summary>
    public Dictionary<PeerId, PeerId> BubbleOwners { get; } = new();

    public PeerId BubbleOwnerOf(PeerId player) => BubbleOwners.TryGetValue(player, out var owner) ? owner : PeerId.Host;

    /// <summary>Becomes a client once the host has welcomed this machine and given it a peer ID.</summary>
    public void BecomeClient(PeerId localPeer)
    {
        Role = SessionRole.Client;
        LocalPeer = localPeer;
    }
}
