using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Serialization;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Session;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Net.Sync;

/// <summary>Replicated state received for an entity, waiting for its tick to be played back.</summary>
public struct ReceivedState
{
    public List<(uint Tick, EntityState State)> Pending;
    /// <summary>The last tick applied: later states only (an older one arriving late would undo a newer one).</summary>
    public uint Applied;
}

/// <summary>
/// Replicates the components registered in <see cref="ReplicatedComponents"/>, each tick after remote bodies are posed:
/// <list type="bullet">
/// <item>Entities this machine has authority over (<see cref="NetOwner.IsLocal"/>): every second tick, each replicated
/// component of the entity and its descendants whose bytes differ from what was last sent goes out, in an
/// <see cref="EntityState"/> per entity (up to <see cref="MaxStateBytes"/>; the rest go next time). Every
/// <see cref="RefreshTicks"/>, at a tick spread by the entity's ID, all of them go again, for lost changes and
/// machines just sent the entity.</item>
/// <item>Entities with authority elsewhere: each state received is applied in tick order once the entity's playback
/// passes its tick (<see cref="RemoteBodySystem.SampleTick"/>, about 10 ticks behind), so the state matches the pose
/// it's drawn at; one without a <see cref="RemoteBody"/> has no playback, and its state is applied as it arrives. A
/// state arriving after a newer one was applied is dropped; the next refresh restores anything it carried.</item>
/// </list>
/// </summary>
public sealed class ReplicationSystem : ISystem, IDebugUiSystem
{
    /// <summary>One entity's state stops taking components before it passes this many bytes, so it fits a frame.</summary>
    public const int MaxStateBytes = 900;

    private readonly SimulationParticipant _net;
    private readonly ReplicatedComponents _components;
    private readonly RemoteBodySystem _remoteBodies;
    private readonly EntitySet _roots;
    private readonly EntitySet _received;
    private readonly NetWriter _scratch = new(64);
    private readonly List<EntityState> _outgoing = new();

    /// <summary>Per entity with authority here: each component's bytes as last sent (since its last refresh), and when it
    /// next refreshes. One this machine has just been given authority over has none, so everything goes: keep it so,
    /// and never seed this from received state, or a hand-off could leave the old authority's stale state in place.</summary>
    private readonly Dictionary<Entity, Sent> _sent = new();
    private readonly HashSet<Entity> _local = new();
    private long _componentsSent, _componentsApplied, _statesDropped;

    private sealed class Sent
    {
        public readonly Dictionary<(ulong Key, ushort Component), byte[]> Bytes = new();
        public uint NextRefresh;
    }

    public ReplicationSystem(SimulationParticipant net, World world, ReplicatedComponents components, RemoteBodySystem remoteBodies)
    {
        _net = net;
        _components = components;
        _remoteBodies = remoteBodies;
        net.Replication = this;
        _roots = world.GetEntities().With<EntityId>().With<NetOwner>().AsSet();
        _received = world.GetEntities().With<ReceivedState>().With<NetOwner>().AsSet();
    }

    /// <summary>Ticks between full refreshes of each entity's replicated state (about a second at 60).</summary>
    public int RefreshTicks { get; set; } = 60;

    public void Update(float dt)
    {
        Apply();
        if (_net.Clock.Tick % 2 == 0) Send();
    }

    private void Send()
    {
        uint tick = _net.Clock.Tick;
        _local.Clear();
        _outgoing.Clear();
        foreach (ref readonly var e in _roots.GetEntities())
            if (e.Get<NetOwner>().IsLocal) _local.Add(e);
        foreach (var e in _sent.Keys)
            if (!_local.Contains(e)) _sent.Remove(e); // if it comes back, everything goes again
        foreach (var root in _local)
        {
            if (!_sent.TryGetValue(root, out var sent))
                _sent[root] = sent = new Sent { NextRefresh = tick + (uint)(root.Get<EntityId>().Value % (uint)System.Math.Max(1, RefreshTicks)) };
            if (tick >= sent.NextRefresh || sent.NextRefresh - tick > (uint)System.Math.Max(2, RefreshTicks)) // due, or the clock jumped back
            {
                sent.Bytes.Clear();
                sent.NextRefresh = tick + (uint)System.Math.Max(2, RefreshTicks);
            }
            var state = new EntityState { Root = root.Get<EntityId>() };
            int bytes = state.EncodedSize;
            if (Collect(root, 0, sent, state, ref bytes))
                foreach (var children in _components.ChildrenOf(root))
                    foreach (var (key, child) in children.ReplicatedChildren)
                        if (!Collect(child, key, sent, state, ref bytes)) break;
            if (state.Components.Count == 0) continue;
            _componentsSent += state.Components.Count;
            _outgoing.Add(state);
        }
        if (_outgoing.Count > 0) _net.SendEntityState(tick, _outgoing);
    }

    /// <summary>Adds <paramref name="entity"/>'s replicated components whose bytes aren't those in
    /// <paramref name="sent"/> to <paramref name="state"/>, recording them there; returns false once
    /// <see cref="MaxStateBytes"/> is reached (the rest stay unsent, for next time).</summary>
    private bool Collect(Entity entity, ulong key, Sent sent, EntityState state, ref int bytes)
    {
        foreach (var component in _components.All)
        {
            _scratch.Clear();
            if (!component.TryWrite(entity, _scratch)) continue;
            var written = _scratch.Written;
            if (sent.Bytes.TryGetValue((key, component.Id), out var last) && written.SequenceEqual(last)) continue;
            var change = new ComponentState(key, component.Id, written.ToArray());
            if (bytes + change.EncodedSize > MaxStateBytes) return false;
            bytes += change.EncodedSize;
            sent.Bytes[(key, component.Id)] = change.Data;
            state.Components.Add(change);
        }
        return true;
    }

    /// <summary>A frame of replicated state from other machines: kept on each entity until it's played back.</summary>
    public void Receive(uint tick, IReadOnlyList<EntityState> states)
    {
        foreach (var s in states)
        {
            if (!_net.Registry.TryGet(s.Root, out var e) || !e.Has<NetOwner>() || e.Get<NetOwner>().IsLocal) continue;
            if (!e.Has<ReceivedState>()) e.Set(new ReceivedState { Pending = new() });
            ref var received = ref e.Get<ReceivedState>();
            if (tick <= received.Applied) { _statesDropped++; continue; }
            int i = received.Pending.Count;
            while (i > 0 && received.Pending[i - 1].Tick > tick) i--;
            received.Pending.Insert(i, (tick, s));
        }
    }

    private void Apply()
    {
        foreach (ref readonly var e in _received.GetEntities())
        {
            ref var received = ref e.Get<ReceivedState>();
            if (e.Get<NetOwner>().IsLocal) { received.Pending.Clear(); continue; } // ours now: we're the truth
            double playback = e.Has<RemoteBody>() ? _remoteBodies.SampleTick(e.Get<RemoteBody>().Buffer) : double.MaxValue;
            int applied = 0;
            foreach (var (tick, state) in received.Pending)
            {
                if (tick > playback) break;
                Apply(e, state);
                received.Applied = tick;
                applied++;
            }
            received.Pending.RemoveRange(0, applied);
        }
    }

    private void Apply(Entity root, EntityState state)
    {
        foreach (var c in state.Components)
        {
            if (_components.Find(c.Component) is not { } component || !_components.TryFind(root, c.Key, out var entity)) continue;
            var r = new NetReader(c.Data);
            component.Read(entity, ref r);
            _componentsApplied++;
        }
    }

    public string DebugName => "Replication";

    public void DrawDebugUi()
    {
        int refresh = RefreshTicks;
        if (ImGui.SliderInt("Full refresh (ticks)", ref refresh, 2, 600)) RefreshTicks = refresh;
        ImGui.Text($"Components: {_components.All.Count}; entities with authority here: {_local.Count}");
        ImGui.Text($"Sent {_componentsSent:N0}, applied {_componentsApplied:N0}, late states dropped {_statesDropped:N0}");
    }
}
