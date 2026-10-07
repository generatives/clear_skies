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

    private record struct ShipReading(Entity Ship, string? Skipped, Vector3 Wind, Vector3 Velocity, Vector3 Relative,
                                      Vector3 Force, float FrontArea);

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
            var body = e.Get<PhysicsBodyComponent>().Body;
            var (pos, rot) = _physics.GetBodyPose(body);
            // Only the owner simulates a grid; everyone else follows its body sync.
            string? skipped = e.Get<DynamicGrid>().Locked ? "locked"
                : e.Has<NetOwner>() && !e.Get<NetOwner>().IsLocal ? "simulated elsewhere"
                : air.Faces is null ? "no drag entries yet" : null;
            if (skipped is not null)
            {
                _ships.Add(new(e, skipped, _wind.Sample(pos), _physics.GetBodyLinearVelocity(body), default, default, front));
                continue;
            }
            var force = Apply(body, pos, rot, air, turns: true, dt, out var relative);
            var velocity = _physics.GetBodyLinearVelocity(body);
            _ships.Add(new(e, null, relative + velocity, velocity, relative, force, front));
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

        // Every ship, the selected one first (the one spawned, edited or walked on last: see GridSelection), marked along
        // with the one piloted or stood on; one with no drag says why.
        Entity aboard = default;
        foreach (ref readonly Entity p in _localPlayer.GetEntities()) aboard = p.Get<Physics.Support.Support>().Supporter;
        bool anySelected = false;
        foreach (var r in _ships)
        {
            if (!r.Ship.Has<SelectedGridComponent>()) continue;
            anySelected = true;
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), $"Selected ship {Name(r.Ship)}");
            ImGui.Text($"Wind there: {r.Wind.Length():0.0} m/s   ship's speed: {r.Velocity.Length():0.0} m/s");
            if (r.Skipped is { } why) ImGui.TextDisabled($"No drag: {why}");
            else ImGui.Text($"Relative air: {r.Relative.Length():0.0} m/s   drag: {r.Force.Length():0} N");
        }
        if (!anySelected) ImGui.TextDisabled("No ship selected (spawn, edit or walk on one to select it)");
        ImGui.Separator();

        ImGui.Text($"Ships: {_ships.Count}");
        foreach (var r in _ships.OrderByDescending(r => r.Ship.Has<SelectedGridComponent>()))
        {
            string id = Name(r.Ship);
            var marks = new List<string>();
            if (r.Ship.Has<SelectedGridComponent>()) marks.Add("selected");
            if (r.Ship.Has<PilotedComponent>()) marks.Add("piloted");
            if (r.Ship == aboard) marks.Add("aboard");
            string mark = marks.Count > 0 ? $" ({string.Join(", ", marks)})" : "";
            if (!ImGui.TreeNodeEx($"{id}{mark}##{id}", ImGuiTreeNodeFlags.DefaultOpen)) continue;
            ImGui.Text($"Wind: {r.Wind.Length():0.0} m/s  ({r.Wind.X:0.0}, {r.Wind.Y:0.0}, {r.Wind.Z:0.0})");
            ImGui.Text($"Ship's speed: {r.Velocity.Length():0.0} m/s");
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

    private static string Name(Entity ship) => ship.Has<EntityId>() ? ship.Get<EntityId>().ToString() : "ship";
}
