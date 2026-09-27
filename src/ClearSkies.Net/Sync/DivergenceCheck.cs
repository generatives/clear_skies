using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Serialization;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Session;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Net.Sync;

/// <summary>
/// Catches handler bugs that let machines drift apart (an Apply that read something local). Every
/// <see cref="IntervalTicks"/> the host hashes each entity it owns (its replicated state: blocks, lever settings, lock,
/// not body state) and sends the hash, with the event number it's as of, to every client that has the entity. A
/// client that has applied the same events hashes its own copy; on a mismatch it asks for a fresh description, which
/// comes back as a spawn event and overwrites its copy. Checks are skipped while the client has predictions pending.
/// </summary>
public sealed class DivergenceCheck : ISystem, IDebugUiSystem
{
    public const int IntervalTicks = 300;

    private readonly NetSession _net;
    private readonly EntitySet _owned;
    private readonly Dictionary<uint, ulong> _toCompare = new();
    private readonly NetWriter _writer = new();

    public DivergenceCheck(NetSession net, World world)
    {
        _net = net;
        _owned = world.GetEntities().With<NetId>().With<NetOwner>().With<OwnPresence>().Without<Chunk>().AsSet();
        net.Divergence = this;
        net.Commands.Descriptions.Described += OnDescribed;
    }

    public long HashesSent { get; private set; }
    public long Checked { get; private set; }
    public long Mismatches { get; private set; }

    public void Update(float dt)
    {
        if (_net is not HostSession host || host.Joined.FirstOrDefault() is null) return;
        if (_net.Clock.Tick % IntervalTicks != 0) return;
        foreach (ref readonly var e in _owned.GetEntities())
            if (e.Get<NetOwner>().IsLocal && !e.Has<Player>()) DescribeRequest.Request(e, DescribePurpose.Hash);
    }

    private void OnDescribed(Description d)
    {
        if ((d.Request.Purpose & DescribePurpose.Hash) == 0) return;
        if (_net is HostSession host)
        {
            // Ours: tell everyone who has it.
            uint number = _net.Commands.LastEventNumber(_net.Session.LocalPeer, d.NetId);
            _writer.Clear();
            _writer.WriteByte((byte)MessageKind.StateHash);
            _writer.WriteUInt32(d.NetId);
            _writer.WriteUInt32(number);
            _writer.WriteUInt64(d.Hash);
            foreach (var peer in host.Joined)
                if (peer.Known.Contains(d.NetId))
                {
                    host.Transport?.Send(peer.Connection, _writer.Written, Transport.Channel.Reliable);
                    HashesSent++;
                }
        }
        else if (_toCompare.Remove(d.NetId, out var theirs))
        {
            Checked++;
            if (d.Hash == theirs) return;
            Mismatches++;
            Console.WriteLine($"[net] entity {d.NetId} has drifted from the host's; resyncing");
            _writer.Clear();
            _writer.WriteByte((byte)MessageKind.SnapshotRequest);
            _writer.WriteUInt32(d.NetId);
            ((ClientSession)_net).SendToHost(_writer.Written, Transport.Channel.Reliable);
        }
    }

    /// <summary>A client: the owner's hash of one of its entities.</summary>
    public void ReceiveHash(PeerId owner, ref NetReader r)
    {
        uint id = r.ReadUInt32(), number = r.ReadUInt32();
        ulong hash = r.ReadUInt64();
        if (!_net.Registry.TryGet(id, out var e)) return;
        if (_net.Commands.PendingCount > 0) return;                        // our predictions would differ anyway
        if (_net.Commands.LastEventNumber(owner, id) != number) return;    // not the same events applied (yet)
        _toCompare[id] = hash;
        DescribeRequest.Request(e, DescribePurpose.Hash);
    }

    /// <summary>The host: a client wants a fresh description of an entity.</summary>
    public void ReceiveSnapshotRequest(PeerId from, ref NetReader r)
    {
        uint id = r.ReadUInt32();
        if (_net.Registry.TryGet(id, out var e)) DescribeRequest.Request(e, DescribePurpose.Send, PeerSet.Of(from));
    }

    public string DebugName => "Divergence check";

    public void DrawDebugUi() =>
        ImGui.Text($"Hashes sent {HashesSent}, checked {Checked}, drifted {Mismatches} (every {IntervalTicks / 60} s)");
}
