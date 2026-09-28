using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Session;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Net.Ownership;

/// <summary>
/// The host's bubble manager: decides who simulates what. Every player has a bubble; everything but player characters
/// belongs to the owner of the bubble it's in.
/// <list type="bullet">
/// <item>Each grid is in one player's part of the world: the player it's already with while it stays in their load
/// window, else the nearest player whose window it's in, else nobody's (the host keeps it, and streaming stores it).
/// So a grid inside two windows keeps the owner it has.</item>
/// <item>Two players' bubbles merge when their contents could touch: the players within
/// <see cref="PlayerMergeDistance"/> (so two bubbles never edit the same terrain), their grids' bounds within
/// <see cref="ObjectMergeDistance"/>, or a player within <see cref="ReachMergeDistance"/> of the other's grid. Merges
/// chain (union-find). A merge splits once everything is <see cref="SplitMargin"/> further apart than that, and not
/// before <see cref="MinMergedSeconds"/>.</item>
/// <item>A merged bubble's owner is whoever already owns most of its grids (the host, then the lowest peer, on a tie),
/// and stays its owner while it stays merged.</item>
/// </list>
/// Runs every <see cref="IntervalTicks"/>; changes go through <see cref="OwnershipSystem.Assign"/>, and the bubble
/// owners (who decides each player's terrain edits) go to everyone.
/// </summary>
public sealed class BubbleManager : ISystem, IDebugUiSystem
{
    public const float DefaultPlayerMergeDistance = 64f, DefaultSplitMargin = 16f;

    public float PlayerMergeDistance { get; set; } = DefaultPlayerMergeDistance;
    public float ObjectMergeDistance { get; set; } = 24f;
    public float ReachMergeDistance { get; set; } = 16f;
    public float SplitMargin { get; set; } = DefaultSplitMargin;
    public float MinMergedSeconds { get; set; } = 5f;
    public int IntervalTicks { get; set; } = 10;

    private readonly HostSession _host;
    private readonly OwnershipSystem _ownership;
    private readonly EntitySet _grids;
    private readonly EntitySet _localPlayers;
    private readonly Dictionary<uint, PeerId> _part = new();
    private readonly Dictionary<(PeerId A, PeerId B), uint> _links = new(); // merged pairs, since which tick
    private readonly Dictionary<PeerId, PeerId> _owners = new();              // each player's bubble owner
    private readonly List<(PeerId Peer, Vector3 Position)> _players = new();
    private readonly List<GridInfo> _gridInfo = new();
    private readonly NetWriter _writer = new();

    private readonly record struct GridInfo(Entity Entity, uint Id, Vector3 Centre, float Radius, PeerId Owner);

    public BubbleManager(HostSession host, OwnershipSystem ownership, World world)
    {
        _host = host;
        _ownership = ownership;
        _grids = world.GetEntities().With<DynamicGrid>().With<ChunkGrid>().With<NetId>().With<NetOwner>().With<Transform>().With<OwnPresence>().AsSet();
        _localPlayers = world.GetEntities().With<LocalPlayer>().With<Player>().With<Transform>().AsSet();
    }

    public IReadOnlyDictionary<PeerId, PeerId> BubbleOwners => _owners;
    public int Merges => _links.Count;
    public long Assignments { get; private set; }

    /// <summary>The part of the world (the player) a grid is in; none outside every load window.</summary>
    public PeerId? PartOf(uint grid) => _part.TryGetValue(grid, out var p) ? p : null;

    public void Update(float dt)
    {
        if (_host.Clock.Tick % (uint)IntervalTicks != 0) return;
        GatherPlayers();
        GatherGrids();
        AssignParts();
        UpdateLinks();
        var groupOwner = ChooseOwners();
        Publish(groupOwner);

        foreach (var g in _gridInfo)
        {
            var want = _part.TryGetValue(g.Id, out var part) ? groupOwner[part] : PeerId.Host;
            if (g.Owner == want || _ownership.InHandover(g.Id)) continue;
            _ownership.Assign(g.Entity, want);
            Assignments++;
        }
    }

    private void GatherPlayers()
    {
        _players.Clear();
        foreach (ref readonly var e in _localPlayers.GetEntities())
        {
            _players.Add((PeerId.Host, ToNumerics(e.Get<Transform>().Position)));
            break;
        }
        foreach (var peer in _host.Joined)
            if (_host.Registry.TryGet(peer.PlayerEntity, out var p) && p.Has<Transform>())
                _players.Add((peer.Peer, ToNumerics(p.Get<Transform>().Position)));
    }

    private void GatherGrids()
    {
        _gridInfo.Clear();
        foreach (ref readonly var e in _grids.GetEntities())
        {
            var (centre, radius) = Bounds(e);
            _gridInfo.Add(new GridInfo(e, e.Get<NetId>().Value, centre, radius, e.Get<NetOwner>().Owner));
        }
        // Forget grids that are gone.
        if (_part.Count > _gridInfo.Count)
        {
            var live = _gridInfo.Select(g => g.Id).ToHashSet();
            foreach (var id in _part.Keys.Where(id => !live.Contains(id)).ToList()) _part.Remove(id);
        }
    }

    /// <summary>A bounding sphere of a grid's loaded chunks, in world space.</summary>
    private static (Vector3 Centre, float Radius) Bounds(Entity e)
    {
        var volume = e.Get<ChunkGrid>().Volume;
        ref readonly var t = ref e.Get<Transform>();
        bool any = false;
        Vector3 min = default, max = default;
        foreach (var (pos, _) in volume.All)
        {
            var lo = new Vector3(pos.X, pos.Y, pos.Z) * ChunkData.Size;
            var hi = lo + new Vector3(ChunkData.Size);
            (min, max) = any ? (Vector3.Min(min, lo), Vector3.Max(max, hi)) : (lo, hi);
            any = true;
        }
        if (!any) return (ToNumerics(t.Position), 0f);
        var centreVoxel = (min + max) / 2;
        var world = volume.VoxelToWorld(t, new Silk.NET.Maths.Vector3D<float>(centreVoxel.X, centreVoxel.Y, centreVoxel.Z));
        return (ToNumerics(world), (max - min).Length() / 2);
    }

    private void AssignParts()
    {
        float window = _host.LoadWindow;
        foreach (var g in _gridInfo)
        {
            // Stays with its player while in their window; a new grid starts with its owner, if they're near.
            var keep = _part.TryGetValue(g.Id, out var part) ? part : g.Owner;
            int index = _players.FindIndex(p => p.Peer == keep);
            if (index >= 0 && Vector3.Distance(_players[index].Position, g.Centre) <= window)
            {
                _part[g.Id] = keep;
                continue;
            }
            PeerId? nearest = null;
            float best = window;
            foreach (var (peer, position) in _players)
            {
                float d = Vector3.Distance(position, g.Centre);
                if (d <= best) (best, nearest) = (d, peer);
            }
            if (nearest is { } n) _part[g.Id] = n;
            else _part.Remove(g.Id);
        }
    }

    /// <summary>Merges pairs of players whose contents could interact, and splits ones that have drifted apart.</summary>
    private void UpdateLinks()
    {
        uint tick = _host.Clock.Tick;
        var present = _players.Select(p => p.Peer).ToHashSet();
        foreach (var key in _links.Keys.Where(k => !present.Contains(k.A) || !present.Contains(k.B)).ToList()) _links.Remove(key);

        for (int i = 0; i < _players.Count; i++)
        for (int j = i + 1; j < _players.Count; j++)
        {
            var (a, pa) = _players[i];
            var (b, pb) = _players[j];
            var key = a.Value < b.Value ? (a, b) : (b, a);
            float slack = Closest(a, pa, b, pb); // how far inside (negative) or outside the merge distances they are
            bool linked = _links.TryGetValue(key, out var since);
            if (!linked && slack <= 0) _links[key] = tick;
            else if (linked && slack > SplitMargin && tick - since >= MinMergedSeconds * 60) _links.Remove(key);
        }
    }

    /// <summary>The smallest margin by which any merge rule between two players' parts is missed (≤ 0: they merge).</summary>
    private float Closest(PeerId a, Vector3 pa, PeerId b, Vector3 pb)
    {
        float slack = Vector3.Distance(pa, pb) - PlayerMergeDistance;
        foreach (var g in _gridInfo)
        {
            if (!_part.TryGetValue(g.Id, out var part)) continue;
            if (part == a) slack = MathF.Min(slack, Vector3.Distance(pb, g.Centre) - g.Radius - ReachMergeDistance);
            else if (part == b) slack = MathF.Min(slack, Vector3.Distance(pa, g.Centre) - g.Radius - ReachMergeDistance);
            else continue;
            foreach (var h in _gridInfo)
                if (part == a && _part.TryGetValue(h.Id, out var other) && other == b)
                    slack = MathF.Min(slack, Vector3.Distance(g.Centre, h.Centre) - g.Radius - h.Radius - ObjectMergeDistance);
        }
        return slack;
    }

    /// <summary>Groups the players (union-find over the links) and picks each group's owner.</summary>
    private Dictionary<PeerId, PeerId> ChooseOwners()
    {
        var parent = _players.ToDictionary(p => p.Peer, p => p.Peer);
        PeerId Find(PeerId x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        foreach (var (a, b) in _links.Keys) parent[Find(a)] = Find(b);

        var result = new Dictionary<PeerId, PeerId>();
        foreach (var group in _players.Select(p => p.Peer).GroupBy(Find))
        {
            var members = group.ToList();
            PeerId owner;
            if (members.Count == 1) owner = members[0];
            else
            {
                // Sticky: the owner they already share, if it's one of them.
                var previous = members.Select(m => _owners.TryGetValue(m, out var o) ? o : PeerId.None).Distinct().ToList();
                if (previous.Count == 1 && members.Contains(previous[0])) owner = previous[0];
                else
                {
                    // Whoever owns most of the group's grids, so the fewest change hands; then the host, then the lowest peer.
                    var counts = members.ToDictionary(m => m, _ => 0);
                    foreach (var g in _gridInfo)
                        if (_part.TryGetValue(g.Id, out var part) && members.Contains(part) && counts.ContainsKey(g.Owner)) counts[g.Owner]++;
                    owner = counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key == PeerId.Host ? 0 : 1).ThenBy(c => c.Key.Value).First().Key;
                }
            }
            foreach (var m in members) result[m] = owner;
        }
        return result;
    }

    private void Publish(Dictionary<PeerId, PeerId> owners)
    {
        bool changed = owners.Count != _owners.Count || owners.Any(p => !_owners.TryGetValue(p.Key, out var o) || o != p.Value);
        if (!changed) return;
        _owners.Clear();
        foreach (var (k, v) in owners) _owners[k] = v;
        _host.Session.BubbleOwners.Clear();
        foreach (var (k, v) in owners) _host.Session.BubbleOwners[k] = v;
        _writer.Clear();
        OwnershipMessages.WriteBubbleOwners(_writer, _owners);
        foreach (var peer in _host.Joined) _host.SendReliable(peer.Peer, _writer.Written);
    }

    private static Vector3 ToNumerics(Silk.NET.Maths.Vector3D<float> v) => new(v.X, v.Y, v.Z);

    public string DebugName => "Bubbles";

    public void DrawDebugUi()
    {
        ImGui.Text($"Players {_players.Count}, merged pairs {_links.Count}, grids {_gridInfo.Count}, reassigned {Assignments}");
        ImGui.Text($"Merge at: players {PlayerMergeDistance:0}, grids {ObjectMergeDistance:0}, reach {ReachMergeDistance:0}; split {SplitMargin:0} further after {MinMergedSeconds:0} s");
        foreach (var (player, owner) in _owners) ImGui.Text($"  player {player.Value}: bubble owner {owner.Value}");
    }
}
