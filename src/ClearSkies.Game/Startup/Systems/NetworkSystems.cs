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

    /// <summary>Body sync, last in the tick.</summary>
    public static void AddLast(GameWorld w, NetSession net, LaggedTransport? lag)
    {
        w.Host.AddSystem(new BodySync(net, w.Host.World, w.Host.Physics), SystemStage.Simulation); // owned bodies, every second tick
        w.Host.RegisterDebugUi(new NetDebugPanel(net, w.Simulation.RemoteBodies, lag));
        if (w.Simulation.Pilot != null) w.Simulation.Pilot.Disabled = () => net.OthersConnected; // pilot mode and flight tuning: single-player only
    }
}
