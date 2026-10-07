using System.Numerics;
using BepuPhysics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Weather;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Air resistance from velocity relative to the moving air, each tick before the physics step, for every body with
/// <see cref="ResistsAir"/> simulated here: unlocked grids this machine owns, and the players it simulates or predicts
/// (walking, not free-flying).
///
/// The wind is sampled once, at the centre of mass. For each body axis, the entry facing into the relative air is
/// picked by the sign of the air's velocity along it (relative to the body), and drag applied at that entry's centre,
/// where the air moves at u = v + ω × r − wind:
/// <code>F = −k · A · |u| · uₙ · n,   τ = r × F,   k = ½ρC_d</code>
/// Full speed times the axis component, not the component squared: with the square, a cube flying at 45° would get
/// about 30% less drag than flying straight. Force acts along the face normal, so a flat panel is pushed sideways in a
/// crosswind (sails, with no lift model), lopsided ships weathervane, and spin is damped through the ω × r term.
///
/// Quadratic drag stepped explicitly can overshoot at high speed, so a tick's change in velocity is capped at the
/// relative air speed: drag can bring a body to the air's speed but never past it.
/// </summary>
public sealed class AirResistanceSystem : ISystem, IDebugUiSystem
{
    private readonly PhysicsWorld _physics;
    private readonly WindField _wind;
    private readonly EntitySet _grids;
    private readonly EntitySet _players;

    /// <summary>½ρC_d (N·s²/m⁴): a typical ship's 25 m² front at 10 m/s meets 500 N, so full lever gives about 10 m/s.</summary>
    public float AirConstant = 0.2f;

    // Diagnostics: the local player's relative air, and every ship's this tick (or why it has none).
    private Vector3 _lastPlayerAir;
    private int _lastPlayers;
    private readonly List<ShipReading> _ships = new();
    private readonly EntitySet _localPlayer;

    private record struct ShipReading(Entity Ship, string? Skipped, Vector3 Relative, Vector3 Force, float FrontArea);

    public AirResistanceSystem(World world, PhysicsWorld physics, WindField wind)
    {
        _physics = physics;
        _wind = wind;
        _grids = world.GetEntities().With<ResistsAir>().With<DynamicGrid>().With<PhysicsBodyComponent>().AsSet();
        _players = world.GetEntities().With<ResistsAir>().With<PlayerInput>().With<CharacterControllerComponent>()
            .Without<FreeFlying>().AsSet();
        _localPlayer = world.GetEntities().With<LocalPlayer>().With<Physics.Support.Support>().AsSet();
    }

    public string DebugName => "Air resistance";

    public void Update(float dt)
    {
        int players = 0;
        _ships.Clear();
        foreach (ref readonly Entity e in _grids.GetEntities())
        {
            ref readonly var air = ref e.Get<ResistsAir>();
            float front = air.Faces is null ? 0f : air.Faces[ResistsAir.Index(2, false)].Area; // the bow faces −z
            if (e.Get<DynamicGrid>().Locked) { _ships.Add(new(e, "locked", default, default, front)); continue; }
            // Only the owner simulates a grid; everyone else follows its body sync.
            if (e.Has<NetOwner>() && !e.Get<NetOwner>().IsLocal) { _ships.Add(new(e, "simulated elsewhere", default, default, front)); continue; }
            if (air.Faces is null) { _ships.Add(new(e, "no drag entries yet", default, default, front)); continue; }
            var body = e.Get<PhysicsBodyComponent>().Body;
            var (pos, rot) = _physics.GetBodyPose(body);
            var force = Apply(body, pos, rot, air, turns: true, dt, out var relative);
            _ships.Add(new(e, null, relative, force, front));
        }

        foreach (ref readonly Entity e in _players.GetEntities())
        {
            var character = e.Get<CharacterControllerComponent>().Character;
            if (character.Suspended) continue;
            var body = character.BodyHandle;
            var (pos, _) = _physics.GetBodyPose(body);
            // Players stay upright: their entries keep to world axes, and turning is left to the character.
            Apply(body, pos, Quaternion.Identity, e.Get<ResistsAir>(), turns: false, dt, out var relative);
            players++;
            if (e.Has<LocalPlayer>()) _lastPlayerAir = relative;
        }
        _lastPlayers = players;
    }

    /// <summary>Applies one tick's drag to <paramref name="body"/>; returns the force, and the air's velocity relative to
    /// the body at its centre of mass.</summary>
    private Vector3 Apply(BodyHandle body, Vector3 pos, Quaternion rot, in ResistsAir air, bool turns, float dt,
                          out Vector3 relative)
    {
        float mass = _physics.GetBodyMass(body);
        var v = _physics.GetBodyLinearVelocity(body);
        var w = turns ? _physics.GetBodyAngularVelocity(body) : Vector3.Zero;
        var wind = _wind.Sample(pos);
        var u0 = v - wind; // the body's velocity through the air (the air meets it at −u0)
        relative = -u0;
        if (mass <= 0f) return Vector3.Zero;

        float k = AirConstant * air.DragScale;
        var force = Vector3.Zero;
        var torque = Vector3.Zero;
        for (int axis = 0; axis < 3; axis++)
        {
            var n = Vector3.Transform(axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ }, rot);
            // Moving towards +n, the air meets the +n faces.
            ref readonly var face = ref air.Faces[ResistsAir.Index(axis, Vector3.Dot(u0, n) >= 0f)];
            if (face.Area <= 0f) continue;
            var r = Vector3.Transform(face.Centroid, rot);
            var u = u0 + Vector3.Cross(w, r);
            var f = -k * face.Area * u.Length() * Vector3.Dot(u, n) * n;
            force += f;
            torque += Vector3.Cross(r, f);
        }

        if (force.LengthSquared() < 1e-8f && torque.LengthSquared() < 1e-8f) return Vector3.Zero; // still air: let it sleep

        // Never past the air's speed in one tick.
        float change = force.Length() * dt / mass;
        float speed = u0.Length();
        float scale = change > speed && change > 0f ? speed / change : 1f;
        _physics.ApplyLinearImpulse(body, force * (dt * scale));
        if (turns) _physics.ApplyAngularImpulse(body, torque * (dt * scale));
        return force * scale;
    }

    public void DrawDebugUi()
    {
        ImGui.SliderFloat("Air constant k = ½ρCd", ref AirConstant, 0f, 2f);
        ImGui.Text($"Players simulated here: {_lastPlayers}");
        ImGui.Text($"Local player's relative air: {_lastPlayerAir.Length():0.0} m/s");
        ImGui.Separator();

        // Every ship: the one being piloted or stood on is marked, and one with no drag says why.
        Entity aboard = default;
        foreach (ref readonly Entity p in _localPlayer.GetEntities()) aboard = p.Get<Physics.Support.Support>().Supporter;
        ImGui.Text($"Ships: {_ships.Count}");
        foreach (var r in _ships)
        {
            string id = r.Ship.Has<EntityId>() ? r.Ship.Get<EntityId>().ToString() : "ship";
            string mark = r.Ship.Has<PilotedComponent>() ? " (piloted)" : r.Ship == aboard ? " (aboard)" : "";
            if (!ImGui.TreeNodeEx($"{id}{mark}##{id}", ImGuiTreeNodeFlags.DefaultOpen)) continue;
            if (r.Skipped is { } why) ImGui.TextDisabled($"No drag: {why}");
            else
            {
                ImGui.Text($"Relative air: {r.Relative.Length():0.0} m/s  ({r.Relative.X:0.0}, {r.Relative.Y:0.0}, {r.Relative.Z:0.0})");
                ImGui.Text($"Drag: {r.Force.Length():0} N");
            }
            ImGui.Text($"Front area: {r.FrontArea:0} m²");
            if (r.FrontArea > 0)
                ImGui.Text($"Top speed in still air at 500 N: {MathF.Sqrt(500f / (AirConstant * r.FrontArea)):0.0} m/s");
            ImGui.TreePop();
        }
    }
}
