using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.ECS;

/// <summary>A player's grapple rope: hooked onto a block of <see cref="Anchor"/> (the world's volume or a ship) at
/// <see cref="LocalPoint"/>, in the anchor's own space, so it moves with a ship. See <see cref="GrappleSystem"/>.</summary>
public struct Grapple
{
    /// <summary>The volume's root entity the rope is hooked onto.</summary>
    public Entity Anchor;

    /// <summary>Where on <see cref="Anchor"/> the rope is hooked, in its space (relative to its Transform).</summary>
    public Vector3D<float> LocalPoint;

    /// <summary>How long the rope is slack: it only pulls when stretched past this.</summary>
    public float Length;

    /// <summary>Where the hook is in the world, with the anchor at <paramref name="anchor"/>.</summary>
    public readonly Vector3D<float> WorldPoint(in Transform anchor) => anchor.Position + Vec.Rotate(anchor.Rotation, LocalPoint);
}

/// <summary>
/// Each tick, before physics: grapple ropes, from every walking player's <see cref="PlayerInput"/>. Pressing R
/// (<see cref="PlayerButtons.Grapple"/>) fires a rope from the eye where the player looks, up to <see cref="Reach"/>, and
/// hooks it onto the first block it meets, on the terrain or a ship; the rope holds while R is held and lets go on
/// release. It also lets go when the player flies, uses a control or pilots, or what it's hooked to is gone.
///
/// The rope isn't a rigid constraint: it's a spring that only pulls, from its hooked length, with a little damping. So
/// it's slack when the player is closer to the hook than that, and stretches a little under load: hanging still, about
/// (gravity / <see cref="Stiffness"/>) ≈ 0.2 blocks, more at the bottom of a fast swing. The pull is a velocity change on
/// the character each tick, and an equal and opposite impulse on a ship it's hooked to that's simulated here, at the
/// hook, so swinging from a ship tugs it a little. While hooked, the character's air control doesn't brake or bleed off
/// its swing (<see cref="Physics.Characters.CharacterControllers.CharacterController.Grappling"/>). The rope passes
/// through everything: nothing collides with it.
///
/// Runs on every machine that simulates the player (the host, and a client predicting its own), from the same input.
/// </summary>
public sealed class GrappleSystem : ISystem
{
    /// <summary>How far a rope reaches, in blocks.</summary>
    public const float Reach = 40f;

    /// <summary>The rope's pull per block of stretch, as an acceleration (per second squared): its spring constant
    /// over the player's mass, so it feels the same at any mass.</summary>
    public const float Stiffness = 80f;

    /// <summary>The rope's damping of stretching and springing back, as an acceleration per unit speed (per second):
    /// about a quarter of critical damping at <see cref="Stiffness"/>, so it's springy but settles.</summary>
    public const float Damping = 4.5f;

    /// <summary>The most the rope pulls, as an acceleration, so a long stretch (a ship pulling away) can't fling the
    /// player.</summary>
    public const float MaximumPull = 150f;

    private readonly PhysicsWorld _physics;
    private readonly EntitySet _players;
    private readonly EntitySet _volumes;
    private readonly List<Entity> _released = new();

    public GrappleSystem(World world, PhysicsWorld physics)
    {
        _physics = physics;
        _players = world.GetEntities().With<Transform>().With<PlayerInput>().With<CharacterControllerComponent>().AsSet();
        _volumes = world.GetEntities().With<ChunkGrid>().With<Transform>().AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _players.GetEntities())
        {
            ref readonly var input = ref e.Get<PlayerInput>();
            bool able = !e.Has<FreeFlying>() && !e.Has<LookLockedComponent>() && !e.Has<Piloting>();
            if (e.Has<Grapple>())
            {
                if (!able || !input.IsHeld(PlayerButtons.Grapple) || !Hooked(e.Get<Grapple>())) _released.Add(e);
                else Pull(e, dt);
            }
            else if (able && input.WasPressed(PlayerButtons.Grapple) && input.Aiming)
                Fire(e, input);
        }
        foreach (var e in _released) Release(e);
        _released.Clear();
    }

    /// <summary>Lets go of the player's rope, if they have one.</summary>
    public static void Release(Entity player)
    {
        if (!player.Has<Grapple>()) return;
        player.Remove<Grapple>();
        if (player.Has<CharacterControllerComponent>()) player.Get<CharacterControllerComponent>().Character.Grappling = false;
    }

    private static bool Hooked(in Grapple g) => g.Anchor.IsAlive && g.Anchor.Has<Transform>();

    private void Fire(Entity player, in PlayerInput input)
    {
        ref readonly var cc = ref player.Get<CharacterControllerComponent>();
        var centre = player.Get<Transform>().Position;
        var eye = centre + new Vector3D<float>(0, cc.Character.EyeOffset(cc.EyeHeight), 0);
        var dir = BlockRaycast.Direction(input.Yaw, input.Pitch);
        if (BlockRaycast.Nearest(_volumes, eye, dir, Reach) is not { } hit) return;

        var at = eye + dir * hit.Distance;
        ref readonly var root = ref hit.Root.Get<Transform>();
        player.Set(new Grapple
        {
            Anchor = hit.Root,
            LocalPoint = Vec.Rotate(Vec.Conjugate(root.Rotation), at - root.Position),
            Length = Vector3D.Distance(centre, at),
        });
        cc.Character.Grappling = true;
    }

    private void Pull(Entity player, float dt)
    {
        ref readonly var g = ref player.Get<Grapple>();
        var character = player.Get<CharacterControllerComponent>().Character;
        var hook = PhysicsConv.ToBepu(g.WorldPoint(g.Anchor.Get<Transform>()));
        var toHook = hook - character.Position;
        float distance = toHook.Length();
        float stretch = distance - g.Length;
        if (stretch <= 0f || distance < 1e-4f) return; // slack

        var along = toHook / distance;
        bool onBody = g.Anchor.Has<PhysicsBodyComponent>();
        var hookVelocity = PhysVec.Zero;
        PhysVec centreOfMass = default;
        if (onBody)
        {
            var body = g.Anchor.Get<PhysicsBodyComponent>().Body;
            centreOfMass = _physics.Simulation.Bodies[body].Pose.Position;
            hookVelocity = _physics.GetBodyLinearVelocity(body)
                         + PhysVec.Cross(_physics.GetBodyAngularVelocity(body), hook - centreOfMass);
        }

        // Springs back towards the hook, damped by how fast the player moves away from it (relative to the hook).
        float away = -PhysVec.Dot(character.LinearVelocity - hookVelocity, along);
        float pull = System.Math.Clamp(Stiffness * stretch + Damping * away, 0f, MaximumPull); // a rope never pushes
        if (pull <= 0f) return;
        var change = along * (pull * dt);
        character.AddVelocity(change);

        // The same pull the other way on a ship simulated here (a copy of one simulated elsewhere isn't ours to push).
        if (onBody && (!g.Anchor.Has<NetOwner>() || g.Anchor.Get<NetOwner>().IsLocal))
        {
            var body = g.Anchor.Get<PhysicsBodyComponent>().Body;
            float mass = _physics.Simulation.Bodies[character.BodyHandle].LocalInertia.InverseMass is var inv and > 0f ? 1f / inv : 0f;
            _physics.ApplyLinearImpulse(body, -change * mass, hook - centreOfMass);
        }
    }
}
