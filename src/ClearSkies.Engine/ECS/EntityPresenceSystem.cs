using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
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
/// terrain interest around it (colliders within <see cref="ColliderRange"/>, and for the local player, drawn out to the
/// view distance).</item>
/// <item>Another player simulated elsewhere: drawn within render distance.</item>
/// <item>A grid owned elsewhere near the local player: a kinematic follower, drawn.</item>
/// <item>Anything else: drawn within render distance, nothing more.</item>
/// </list>
/// Terrain is drawn and collided with around the interests by the chunk loader (see <see cref="ChunkLoadSystem"/>).
///
/// Everything else (chunks and block entities) inherits <see cref="Rendered"/> from its nearest ancestor with
/// <see cref="OwnPresence"/>: when this system changes it, it's carried down; a child attached later takes it in
/// <see cref="Hierarchy.SetParent(Entity, Entity)"/>. Physics stays on the grid itself.
/// </summary>
public sealed class EntityPresenceSystem : ISystem, IDebugUiSystem
{
    /// <summary>The collider radius of the terrain interests this system gives: terrain colliders are built within
    /// this distance of a body simulated here...</summary>
    public const float ColliderRange = 192f;

    /// <summary>Hysteresis on distance rules: a layer added within distance d is removed only past d × this.</summary>
    public const float Hysteresis = 1.1f;

    private readonly Session _session;
    private readonly EntitySet _roots;          // grids and players
    private readonly EntitySet _localPlayers;

    /// <param name="viewDistance">How far (horizontally) the local player's terrain is drawn.</param>
    public EntityPresenceSystem(World world, Session session, float viewDistance)
    {
        _session = session;
        ViewDistance = viewDistance;
        _roots = world.GetEntities().With<OwnPresence>().With<Transform>().AsSet();
        _localPlayers = world.GetEntities().With<LocalPlayer>().With<Transform>().AsSet();
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

    public void Update(float dt)
    {
        bool hasLocal = TryLocalPlayer(out var local);

        foreach (ref readonly Entity e in _roots.GetEntities())
            DecideRoot(e, hasLocal, local);
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
        bool owned = e.SimulatedHere() || e.Has<LocalPlayer>();
        bool isPlayer = e.Has<Player>();
        float distance = hasLocal ? Vector3.Distance(ToNumerics(e.Get<Transform>().Position), local) : 0f;

        // Physics.
        PhysicsMode? mode = null;
        if (owned) mode = PhysicsMode.Simulated;
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
            var interest = new TerrainInterest { ColliderRadius = ColliderRange, DrawRadius = e.Has<LocalPlayer>() ? ViewDistance : 0 };
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

    private static Vector3 ToNumerics(Silk.NET.Maths.Vector3D<float> v) => new(v.X, v.Y, v.Z);

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Presence";

    public void DrawDebugUi()
    {
        ImGui.Text($"Session: {_session.Role}, {_session.LocalPeer}");
        ImGui.Text($"Entity render distance: {RenderDistance:0} (view distance {ViewDistance:0}, limit {RenderDistanceLimit:0})");
        foreach (ref readonly Entity e in _roots.GetEntities())
        {
            string id = e.Has<EntityId>() ? e.Get<EntityId>().ToString() : "-";
            string kind = e.Has<Player>() ? "player" : "grid";
            string physics = e.Has<PhysicsPresence>() ? e.Get<PhysicsPresence>().Mode.ToString() : "none";
            string owner = e.Has<NetOwner>() ? (e.Get<NetOwner>().IsLocal ? "here" : e.Get<NetOwner>().Owner.ToString()) : "here";
            ImGui.Text($"  {kind} {id}: owner {owner}, physics {physics}, rendered {e.Has<Rendered>()}, " +
                       $"terrain {(e.Has<TerrainInterest>() ? e.Get<TerrainInterest>().ToString() : "none")}");
        }
    }
}
