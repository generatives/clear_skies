namespace ClearSkies.Engine.Entities;

public enum SessionRole
{
    /// <summary>The hosting machine's: authority over every entity (the Host itself, which keeps the save and streams
    /// entities, has no world). Single-player is hosting with nobody else able to join.</summary>
    Host,
    Client,
}

/// <summary>This machine's place in the session. Single-player is a <see cref="SessionRole.Host"/> session nobody else can
/// join. The role decides which systems are set up; gameplay checks <see cref="NetOwner.IsLocal"/> instead.</summary>
public sealed class Session
{
    public Session(SessionRole role, PeerId localPeer)
    {
        Role = role;
        LocalPeer = localPeer;
    }

    /// <summary>The hosting machine's session, before it has joined its Host (or with no network at all, in tests).</summary>
    public static Session SinglePlayer() => new(SessionRole.Host, PeerId.Host);

    public SessionRole Role { get; private set; }
    public PeerId LocalPeer { get; private set; }
    public bool IsHost => Role == SessionRole.Host;

    /// <summary>An owner record for something this machine owns.</summary>
    public NetOwner LocalOwner(ushort epoch = 0) => new() { Owner = LocalPeer, Epoch = epoch, IsLocal = true };

    /// <summary>An owner record for <paramref name="owner"/>, local or not.</summary>
    public NetOwner OwnerFor(PeerId owner, ushort epoch = 0) => new() { Owner = owner, Epoch = epoch, IsLocal = owner == LocalPeer };

    /// <summary>Joined a game: the Host has welcomed this machine and given it a peer ID (<see cref="PeerId.Host"/> for
    /// the hosting machine's, which has authority).</summary>
    public void Join(PeerId localPeer)
    {
        Role = localPeer == PeerId.Host ? SessionRole.Host : SessionRole.Client;
        LocalPeer = localPeer;
    }
}
