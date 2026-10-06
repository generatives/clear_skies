using System.Numerics;
using ClearSkies.Engine.Entities;
using ClearSkies.Net.Protocol;

namespace ClearSkies.Net.Session;

/// <summary>
/// What a Participant can tell the Host, as one Participant: the Host knows who's calling. On the hosting machine, a
/// <see cref="LocalHost"/>, which calls the Host directly; on any other, a <see cref="RemoteHost"/>, which carries each
/// call over the network (to <see cref="HostNetwork"/>, which makes it on the Host).
/// </summary>
public interface IHost
{
    /// <summary>Asks the Host's tick (answered with <see cref="IParticipant.Pong"/>).</summary>
    void Ping(double clientTimeMs);

    /// <summary>Its entity IDs are running low (answered with <see cref="IParticipant.IdBlock"/>).</summary>
    void RequestIdBlock();

    /// <summary>The space it wants to see (around its player, or where its player will spawn). The first tells the Host
    /// it's up.</summary>
    void SetView(Vector3 centre, float radius);

    /// <summary>A command, to its authority.</summary>
    void SendCommand(in CommandMessage command);

    /// <summary>An event, to everyone who has its entity.</summary>
    void SendEvent(in EventMessage evt);

    /// <summary>A command it turned down, to whoever sent it.</summary>
    void Reject(in Rejection rejection);

    /// <summary>Its player's latest inputs, to their authority.</summary>
    void SendInput(in PlayerInputMessage input);

    /// <summary>Snapshots of the bodies it simulates, to everyone who has them.</summary>
    void SendFrame(uint tick, IReadOnlyList<BodySnapshot> snapshots);

    /// <summary>The authority made an entity (see <see cref="DescriptionMessage"/>).</summary>
    void EntityCreated(in DescriptionMessage description);

    /// <summary>The authority's answer to <see cref="IParticipant.Describe"/>.</summary>
    void EntityDescribed(in DescriptionMessage description);

    /// <summary>The authority's answer to <see cref="IParticipant.Release"/>: the entity's last Description.</summary>
    void EntityReleased(in DescriptionMessage description);

    /// <summary>The authority despawned an entity: the Host stops keeping it.</summary>
    void EntityDeleted(EntityId id);

    /// <summary>The authority's answer to <see cref="IParticipant.Save"/>: one entity's Description, of everything.</summary>
    void EntitySaved(in DescriptionMessage description);

    /// <summary>The authority has described everything for the save.</summary>
    void SaveDone();

    /// <summary>It's leaving, and why.</summary>
    void Leave(string reason);
}

/// <summary>
/// What the Host tells a Participant. On the hosting machine, the <see cref="SimulationParticipant"/> itself; for any
/// other, the Host's end of the network (<see cref="HostNetwork"/>), which carries each call there.
/// </summary>
public interface IParticipant
{
    /// <summary>An entity coming into its world.</summary>
    void Spawn(in SpawnMessage spawn);

    /// <summary>An entity released: its copy goes.</summary>
    void Forget(EntityId id);

    void ReceiveEvent(in EventMessage evt);
    void ReceiveCommand(in CommandMessage command);
    void Rejected(in Rejection rejection);
    void ReceiveFrame(uint tick, IReadOnlyList<BodySnapshot> snapshots);
    void Pong(in TimePong pong);
    void IdBlock(uint first, uint count);

    /// <summary>Someone joined or left (for the UI: their player comes and goes by spawn and forget).</summary>
    void PlayerNotice(in PlayerNotice notice);

    /// <summary>The Host has let it go, and why.</summary>
    void Disconnected(string reason);

    /// <summary>The authority's: a player's latest inputs, from the machine that plays them.</summary>
    void ReceiveInput(in PlayerInputMessage input);

    /// <summary>The authority's: describe an entity a last time (<see cref="IHost.EntityReleased"/>), then despawn it.</summary>
    void Release(EntityId id);

    /// <summary>The authority's: describe an entity now (<see cref="IHost.EntityDescribed"/>), for a Participant that
    /// doesn't have it.</summary>
    void Describe(EntityId id);

    /// <summary>The authority's: describe everything now (<see cref="IHost.EntitySaved"/>, then
    /// <see cref="IHost.SaveDone"/>), for the save.</summary>
    void Save();
}
