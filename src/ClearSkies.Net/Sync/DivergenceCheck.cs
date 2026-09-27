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
/// <see cref="IntervalTicks"/> each machine hashes the entities it owns (their replicated state: blocks, lever settings,
/// lock, not body state) and sends the hash, with the epoch and event number it's as of, to every machine that has the
/// entity (a client's go through the host, which checks them too). A machine that has applied the same events hashes its
/// own copy; on a mismatch it asks the owner for a fresh description (through the host), which the owner sends as a
/// spawn event and which overwrites the copy. Checks are skipped while predictions are pending.
/// </summary>
public sealed class DivergenceCheck : ISystem, IDebugUiSystem
{
    public const int IntervalTicks = 300;

    private readonly NetSession _net;
    private readonly EntitySet _owned;
    private readonly Dictionary<uint, (PeerId Owner, ulong Hash)> _toCompare = new();
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
        if (!_net.OthersConnected) return;
        if (_net.Clock.Tick % IntervalTicks != 0) return;
        foreach (ref readonly var e in _owned.GetEntities())
            if (e.Get<NetOwner>().IsLocal && !e.Has<Player>()) DescribeRequest.Request(e, DescribePurpose.Hash);
    }

    private void OnDescribed(Description d)
    {
        if ((d.Request.Purpose & DescribePurpose.Send) != 0 && _net is ClientSession)
        {
            // A client owner asked for a fresh copy of its entity: it goes to everyone who has it, as a spawn event.
            _net.BroadcastEvent(d.HandlerId, _net.Commands.StampEvent(d.NetId), d.Payload);
        }
        if ((d.Request.Purpose & DescribePurpose.Hash) == 0) return;
        if (d.Entity.Get<NetOwner>().IsLocal)
        {
            // Ours: tell everyone who has it.
            var owner = d.Entity.Get<NetOwner>();
            WriteHash(d.NetId, _net.Session.LocalPeer, owner.Epoch, _net.Commands.LastEventNumber(_net.Session.LocalPeer, d.NetId), d.Hash);
            switch (_net)
            {
                case HostSession host:
                    foreach (var peer in host.Joined)
                        if (peer.Known.Contains(d.NetId)) { host.SendReliable(peer.Peer, _writer.Written); HashesSent++; }
                    break;
                case ClientSession client:
                    client.SendToHost(_writer.Written, Transport.Channel.Reliable);
                    HashesSent++;
                    break;
            }
        }
        else if (_toCompare.Remove(d.NetId, out var theirs))
        {
            Checked++;
            if (d.Hash == theirs.Hash) return;
            Mismatches++;
            Console.WriteLine($"[net] entity {d.NetId} has drifted from its owner's; resyncing");
            _writer.Clear();
            _writer.WriteByte((byte)MessageKind.SnapshotRequest);
            _writer.WriteUInt32(d.NetId);
            switch (_net)
            {
                case ClientSession client: client.SendToHost(_writer.Written, Transport.Channel.Reliable); break;
                case HostSession host: host.SendReliable(theirs.Owner, _writer.Written); break; // straight to the owner
            }
        }
    }

    private void WriteHash(uint id, PeerId owner, ushort epoch, uint number, ulong hash)
    {
        _writer.Clear();
        _writer.WriteByte((byte)MessageKind.StateHash);
        _writer.WriteUInt32(id);
        _writer.WriteUInt32(owner.Value);
        _writer.WriteUInt16(epoch);
        _writer.WriteUInt32(number);
        _writer.WriteUInt64(hash);
    }

    /// <summary>An owner's hash of one of its entities (on the host, from a client: checked here and passed on).</summary>
    public void ReceiveHash(ref NetReader r)
    {
        uint id = r.ReadUInt32();
        var owner = new PeerId(r.ReadUInt32());
        ushort epoch = r.ReadUInt16();
        uint number = r.ReadUInt32();
        ulong hash = r.ReadUInt64();
        if (!_net.Registry.TryGet(id, out var e) || !e.Has<NetOwner>()) return;
        if (_net is HostSession host)
        {
            WriteHash(id, owner, epoch, number, hash);
            foreach (var peer in host.Joined)
                if (peer.Peer != owner && peer.Known.Contains(id)) host.SendReliable(peer.Peer, _writer.Written);
        }
        var current = e.Get<NetOwner>();
        if (current.Owner != owner || current.Epoch != epoch) return;       // changed hands since
        if (_net.Commands.PendingCount > 0) return;                         // our predictions would differ anyway
        if (_net.Commands.LastEventNumber(owner, id) != number) return;     // not the same events applied (yet)
        _toCompare[id] = (owner, hash);
        DescribeRequest.Request(e, DescribePurpose.Hash);
    }

    /// <summary>Someone wants a fresh description of an entity: the owner sends it (the host, to the asker; a client,
    /// to everyone who has it). The host passes requests for a client's entities on to it.</summary>
    public void ReceiveSnapshotRequest(PeerId from, ref NetReader r)
    {
        uint id = r.ReadUInt32();
        if (!_net.Registry.TryGet(id, out var e) || !e.Has<NetOwner>()) return;
        var owner = e.Get<NetOwner>();
        if (owner.IsLocal)
        {
            DescribeRequest.Request(e, DescribePurpose.Send, _net is HostSession ? PeerSet.Of(from) : default);
            return;
        }
        if (_net is HostSession host)
        {
            _writer.Clear();
            _writer.WriteByte((byte)MessageKind.SnapshotRequest);
            _writer.WriteUInt32(id);
            host.SendReliable(owner.Owner, _writer.Written);
        }
    }

    public string DebugName => "Divergence check";

    public void DrawDebugUi() =>
        ImGui.Text($"Hashes sent {HashesSent}, checked {Checked}, drifted {Mismatches} (every {IntervalTicks / 60} s)");
}
