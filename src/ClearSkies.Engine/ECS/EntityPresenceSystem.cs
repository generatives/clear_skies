using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Decides the presence layers (<see cref="PhysicsPresence"/>, <see cref="Rendered"/>, <see cref="TerrainInterest"/>)
/// of every entity with <see cref="OwnPresence"/>, each tick. Streaming systems only decide what exists; this decides
/// how what exists is hosted here. Every decision has hysteresis so nothing flickers.
///
/// Grids and players:
/// <list type="bullet">
/// <item>Owned here, or the local player (simulated here to predict them): simulated, drawn within render distance,
/// terrain interest around it (the local player's is <see cref="TerrainInterestKind.Full"/>, anything else's colliders
/// only).</item>
/// <item>Another player simulated elsewhere: drawn within render distance.</item>
/// <item>A grid owned elsewhere near the local player: a kinematic follower, drawn.</item>
/// <item>Anything else: drawn within render distance, nothing more.</item>
/// </list>
/// Terrain chunks (children of the world volume, each with its own <see cref="OwnPresence"/>): static colliders within
/// <see cref="ColliderRange"/> of any terrain interest (dropped past <see cref="ColliderDropRange"/>), and drawn within
/// the local player's view distance.
///
/// Everything else (a grid's chunks and block entities) inherits <see cref="Rendered"/> from its nearest ancestor with
/// <see cref="OwnPresence"/>: when this system changes it, it's carried down; a child attached later takes it in
/// <see cref="Hierarchy.SetParent(Entity, Entity)"/>. Physics stays on the grid itself.
/// </summary>
public sealed class EntityPresenceSystem : ISystem, IDebugUiSystem
{
    /// <summary>Terrain colliders are built within this distance of any terrain interest...</summary>
    public const float ColliderRange = 192f;

    /// <summary>...and dropped past this one.</summary>
    public const float ColliderDropRange = 256f;

    /// <summary>Hysteresis on distance rules: a layer added within distance d is removed only past d × this.</summary>
    public const float Hysteresis = 1.1f;

    private const int S = ChunkData.Size;
    private const int SweepTicks = 30;

    private readonly Session _session;
    private readonly ChunkVolume _worldVolume;
    private readonly EntitySet _roots;          // grids and players
    private readonly EntitySet _newTerrain;     // terrain chunks not decided yet
    private readonly EntitySet _terrainColliders;
    private readonly EntitySet _interests;
    private readonly EntitySet _localPlayers;
    private readonly List<Vector3> _centres = new();
    private readonly List<Entity> _scratch = new();
    private readonly (int dx, int dy, int dz)[] _rangeOffsets;
    private int _sweep;

    /// <param name="viewDistance">How far (horizontally) the local player's terrain is drawn.</param>
    public EntityPresenceSystem(World world, Session session, ChunkVolume worldVolume, float viewDistance)
    {
        _session = session;
        _worldVolume = worldVolume;
        ViewDistance = viewDistance;
        _roots = world.GetEntities().With<OwnPresence>().With<Transform>().Without<Chunk>().AsSet();
        _newTerrain = world.GetEntities().With<OwnPresence>().With<Chunk>().WhenAdded<Chunk>().AsSet();
        _terrainColliders = world.GetEntities().With<Chunk>().With<PhysicsPresence>().With<OwnPresence>().AsSet();
        _interests = world.GetEntities().With<TerrainInterest>().With<Transform>().AsSet();
        _localPlayers = world.GetEntities().With<LocalPlayer>().With<Transform>().AsSet();
        int r = (int)MathF.Ceiling(ColliderRange / S);
        var offsets = new List<(int, int, int)>();
        for (int dz = -r; dz <= r; dz++) for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++)
            offsets.Add((dx, dy, dz));
        _rangeOffsets = offsets.ToArray();
    }

    /// <summary>How far away grids and players are drawn: as far as the terrain (<see cref="ViewDistance"/>), up to
    /// <see cref="RenderDistanceLimit"/>.</summary>
    public float RenderDistance => MathF.Min(ViewDistance, RenderDistanceLimit);

    /// <summary>The furthest grids and players are drawn at any view distance (e.g. the entity load window, past which
    /// there's nothing to draw). Unlimited unless set.</summary>
    public float RenderDistanceLimit { get; set; } = float.PositiveInfinity;

    /// <summary>A grid owned elsewhere is a kinematic follower within this distance of the local player.</summary>
    public float FollowerGridRange { get; set; } = 512f;

    /// <summary>How far the local player's terrain is drawn (horizontal).</summary>
    public float ViewDistance { get; set; }

    /// <summary>Whether the terrain around a point has loaded with colliders. A grid owned here only gets a body once
    /// it has, so nothing loaded from storage falls through the world. Null: always ready.</summary>
    public Func<Vector3, bool>? TerrainReady { get; set; }

    public void Update(float dt)
    {
        bool hasLocal = TryLocalPlayer(out var local);

        foreach (ref readonly Entity e in _roots.GetEntities())
            DecideRoot(e, hasLocal, local);

        foreach (ref readonly Entity e in _newTerrain.GetEntities())
            SetRendered(e, !hasLocal || TerrainVisible(e.Get<Chunk>().Entry.Position, local, ViewDistance));
        _newTerrain.Complete();

        UpdateTerrainColliders();

        if (++_sweep >= SweepTicks)
        {
            _sweep = 0;
            SweepTerrain(hasLocal, local);
        }
    }

    private bool TryLocalPlayer(out Vector3 position)
    {
        foreach (ref readonly Entity e in _localPlayers.GetEntities())
        {
            position = ToNumerics(e.Get<Transform>().Position);
            return true;
        }
        position = default;
        return false;
    }

    private void DecideRoot(Entity e, bool hasLocal, Vector3 local)
    {
        // The local player is simulated here even when the host owns them: to predict them.
        bool owned = !e.Has<NetOwner>() || e.Get<NetOwner>().IsLocal || e.Has<LocalPlayer>();
        bool isPlayer = e.Has<Player>();
        float distance = hasLocal ? Vector3.Distance(ToNumerics(e.Get<Transform>().Position), local) : 0f;

        // Physics.
        PhysicsMode? mode = null;
        if (owned)
        {
            bool ready = isPlayer || e.Has<PhysicsPresence>() || TerrainReady is null ||
                         TerrainReady(ToNumerics(e.Get<Transform>().Position));
            if (ready) mode = PhysicsMode.Simulated;
        }
        else if (!isPlayer && Within(distance, FollowerGridRange, e.Has<PhysicsPresence>())) mode = PhysicsMode.KinematicFollower;
        if (mode is { } m)
        {
            if (!e.Has<PhysicsPresence>() || e.Get<PhysicsPresence>().Mode != m) e.Set(new PhysicsPresence { Mode = m });
        }
        else if (e.Has<PhysicsPresence>()) e.Remove<PhysicsPresence>();

        // Rendering.
        SetRendered(e, Within(distance, RenderDistance, e.Has<Rendered>()));

        // Terrain interest.
        if (owned)
        {
            var interest = e.Has<LocalPlayer>()
                ? new TerrainInterest { Radius = ViewDistance, Kind = TerrainInterestKind.Full }
                : new TerrainInterest { Radius = ColliderRange, Kind = TerrainInterestKind.CollidersOnly };
            if (!e.Has<TerrainInterest>() || !e.Get<TerrainInterest>().Equals(interest)) e.Set(interest);
        }
        else if (e.Has<TerrainInterest>()) e.Remove<TerrainInterest>();
    }

    private static bool Within(float distance, float range, bool already)
        => distance <= (already ? range * Hysteresis : range);

    /// <summary>Adds or removes <see cref="Rendered"/> on an entity and every descendant that inherits it.</summary>
    public static void SetRendered(Entity e, bool rendered)
    {
        if (rendered == e.Has<Rendered>()) return;
        if (rendered) e.Set<Rendered>(); else e.Remove<Rendered>();
        Hierarchy.PropagateRendered(e);
    }

    // ── terrain ──────────────────────────────────────────────────────────────

    private static bool TerrainVisible(ChunkPosition pos, Vector3 local, float viewDistance)
    {
        var centre = new Vector2(pos.X * S + S / 2f, pos.Z * S + S / 2f);
        return Vector2.Distance(centre, new Vector2(local.X, local.Z)) <= viewDistance * Hysteresis + S;
    }

    private void GatherCentres()
    {
        _centres.Clear();
        foreach (ref readonly Entity e in _interests.GetEntities())
            _centres.Add(ToNumerics(e.Get<Transform>().Position));
    }

    /// <summary>Distance squared from chunk <paramref name="pos"/>'s box to the nearest terrain interest.</summary>
    private float NearestCentreSq(ChunkPosition pos)
    {
        var lo = new Vector3(pos.X * S, pos.Y * S, pos.Z * S);
        float best = float.MaxValue;
        foreach (var c in _centres)
        {
            var d = Vector3.Max(Vector3.Max(lo - c, c - (lo + new Vector3(S))), Vector3.Zero);
            best = MathF.Min(best, d.LengthSquared());
        }
        return best;
    }

    /// <summary>Gives static colliders to loaded terrain chunks within range of an interest.</summary>
    private void UpdateTerrainColliders()
    {
        GatherCentres();
        foreach (var c in _centres)
        {
            int cx = (int)MathF.Floor(c.X / S), cy = (int)MathF.Floor(c.Y / S), cz = (int)MathF.Floor(c.Z / S);
            foreach (var (dx, dy, dz) in _rangeOffsets)
            {
                if (_worldVolume.GetEntry(new ChunkPosition(cx + dx, cy + dy, cz + dz)) is not { } entry) continue;
                var chunk = entry.Entity;
                if (chunk.Has<PhysicsPresence>() || !chunk.Has<OwnPresence>()) continue;
                if (NearestCentreSq(entry.Position) > ColliderRange * ColliderRange) continue;
                chunk.Set(new PhysicsPresence { Mode = PhysicsMode.Static });
            }
        }
    }

    /// <summary>Drops colliders out of range of every interest, and redoes drawn-or-not for moved views.</summary>
    private void SweepTerrain(bool hasLocal, Vector3 local)
    {
        _scratch.Clear();
        foreach (ref readonly Entity e in _terrainColliders.GetEntities())
            if (NearestCentreSq(e.Get<Chunk>().Entry.Position) > ColliderDropRange * ColliderDropRange) _scratch.Add(e);
        foreach (var e in _scratch) e.Remove<PhysicsPresence>();

        if (!hasLocal) return;
        foreach (var (pos, entry) in _worldVolume.All)
        {
            var e = entry.Entity;
            if (e.Has<OwnPresence>()) SetRendered(e, TerrainVisible(pos, local, ViewDistance));
        }
    }

    private static Vector3 ToNumerics(Silk.NET.Maths.Vector3D<float> v) => new(v.X, v.Y, v.Z);

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Presence";

    public void DrawDebugUi()
    {
        ImGui.Text($"Session: {_session.Role}, {_session.LocalPeer}");
        ImGui.Text($"Entity render distance: {RenderDistance:0} (view distance {ViewDistance:0}, limit {RenderDistanceLimit:0})");
        ImGui.Text($"Terrain chunks with colliders: {_terrainColliders.Count}   Interests: {_interests.Count}");
        foreach (ref readonly Entity e in _roots.GetEntities())
        {
            string id = e.Has<EntityId>() ? e.Get<EntityId>().ToString() : "-";
            string kind = e.Has<Player>() ? "player" : "grid";
            string physics = e.Has<PhysicsPresence>() ? e.Get<PhysicsPresence>().Mode.ToString() : "none";
            string owner = e.Has<NetOwner>() ? (e.Get<NetOwner>().IsLocal ? "here" : e.Get<NetOwner>().Owner.ToString()) : "here";
            ImGui.Text($"  {kind} {id}: owner {owner}, physics {physics}, rendered {e.Has<Rendered>()}, " +
                       $"terrain {(e.Has<TerrainInterest>() ? e.Get<TerrainInterest>().Kind.ToString() : "none")}");
        }
    }
}
