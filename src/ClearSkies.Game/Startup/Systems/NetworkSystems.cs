using ClearSkies.Engine.Core;
using ClearSkies.Net.Debug;
using ClearSkies.Net.Session;
using ClearSkies.Net.Sync;
using ClearSkies.Net.Transport;

namespace ClearSkies.Game.Startup.Systems;

/// <summary>The network session, first in each tick (everything that arrived: commands, events, snapshots, session
/// messages), and body sync, last in it; and the network panel.</summary>
public static class NetworkSystems
{
    public static void AddFirst(GameWorld w, NetSession net) => w.Host.AddSystem(net, SystemStage.Simulation);

    /// <summary>Body sync, last in the tick; returns what draws bodies owned elsewhere, for the frame.</summary>
    public static RemoteBodySystem AddLast(GameWorld w, NetSession net, LaggedTransport? lag)
    {
        w.Host.AddSystem(new BodySync(net, w.Host.World, w.Host.Physics), SystemStage.Simulation); // owned bodies, every second tick
        var remoteBodies = new RemoteBodySystem(w.Host.World, w.Registry, w.Host.Clock);
        w.Host.Gui.RegisterDebugUi(new NetDebugPanel(net, remoteBodies, lag));
        w.Simulation.Pilot.Disabled = () => net.OthersConnected; // pilot mode and flight tuning: single-player only
        return remoteBodies;
    }
}
