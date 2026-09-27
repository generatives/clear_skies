using ClearSkies.Engine.Core;

namespace ClearSkies.Net.Session;

/// <summary>First in each tick: receives everything that arrived (commands and events into the command queue, body
/// snapshots into buffers, session messages to the session).</summary>
public sealed class NetReceiveSystem : ISystem
{
    private readonly NetSession _net;
    public NetReceiveSystem(NetSession net) => _net = net;
    public void Update(float dt) => _net.Receive();
}

/// <summary>Last in each tick: per-tick housekeeping after everything's been sent.</summary>
public sealed class NetSendSystem : ISystem
{
    private readonly NetSession _net;
    public NetSendSystem(NetSession net) => _net = net;
    public void Update(float dt) => _net.Flush();
}
