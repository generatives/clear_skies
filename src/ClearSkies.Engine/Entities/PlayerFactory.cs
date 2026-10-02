using System.Numerics;
using BepuPhysics.Collidables;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Characters;
using ClearSkies.Engine.Physics.Support;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Entities;

/// <summary>Builds player entities from descriptions: a construction helper for SpawnPlayerHandler, the only caller.
/// Local and remote players alike get a character body; only the local one gets input and the camera.</summary>
public static class PlayerFactory
{
    public const float EyeHeight = 0.7f;
    public const float LookSensitivity = 0.0025f;

    public static Entity Create(World world, PhysicsWorld physics, EntityId id, NetOwner owner, PlayerDescription d)
    {
        var player = world.CreateEntity();
        player.Set(new Transform { Position = PhysicsConv.ToSilk(d.Position), Rotation = Quaternion<float>.Identity, Scale = Vector3D<float>.One });
        player.Set(new MouseLookComponent { LookSensitivity = LookSensitivity });
        player.Set(new FreeFlyController { MoveSpeed = d.FlySpeed });

        // Only the player's own machine simulates their character; everyone else draws them from body sync.
        if (owner.IsLocal)
            player.Set(new CharacterControllerComponent { Character = CreateCharacter(physics, d.Position, player), EyeHeight = EyeHeight });
        player.Set(new Support());
        player.Set(new Player { Id = d.Id, Name = d.Name, IsLocal = owner.IsLocal });
        player.Set(id);
        player.Set(owner);
        player.Set<OwnPresence>();
        if (owner.IsLocal)
        {
            player.Set(new InterpolatedTransform { PositionOnly = true }); // moved by ticks, turned per frame by mouse-look
            player.Set<LocalPlayer>();
            player.Set(new PlayerInput()); // filled each tick by InputSampleSystem
        }
        else player.Set(new InterpolatedTransform()); // moved and turned each tick by their snapshots
        Fill(player, d);
        return player;
    }

    /// <summary>A player's character body at <paramref name="position"/>: the local player's, and the copies of other
    /// players that stand on ships here (see FollowerSystem), so both press on a deck alike.</summary>
    public static PlayerCharacter CreateCharacter(PhysicsWorld physics, Vector3 position, Entity entity) =>
        new(physics.Characters, position, new Capsule(radius: 0.3f, length: 1.0f),
            // Light (two Wood blocks' worth): the character pushes off the deck it walks on as hard as it pushes
            // itself, so a heavy character with strong forces shoved and twisted ships as hard as their Fans.
            minimumSpeculativeMargin: 0.01f, mass: 2f,
            // Sharp start/stop: accel = force/mass = 50 m/s², reaches the 5 m/s target in ~0.1s (same
            // cap governs stopping) and gives the motion constraint plenty of headroom to hold the
            // character's velocity to an accelerating support (e.g. a thrusting airship deck) without
            // lagging behind. MaximumVerticalGlueForce raised to match for the vertical half of that grip.
            // Both scale with the mass, so the feel stays the same at any mass.
            maximumHorizontalForce: 100f, maximumVerticalGlueForce: 70f,
            // JumpVelocity paired with PlayerCharacter's default ExtraFallGravity (12, on top of the
            // world's own gentle -6 gravity -> 18 effective while airborne) for a ~1-block peak jump
            // height: v²/(2·g) = 6²/(2·18) = 1.0. Also makes falls heavier/snappier instead of floaty.
            jumpVelocity: 6f, speed: 5f,
            // Full air control: same acceleration and top speed as on the ground, and a gentle brake with no keys
            // held, so you can steer mid-air and let go to avoid overshooting a ledge.
            airControlForceScale: 1f, airControlSpeedScale: 1f, airBrakeScale: 0.5f, entity: entity);

    /// <summary>Puts an existing player where <paramref name="d"/> says, facing that way, in that mode.</summary>
    private static void Fill(Entity player, PlayerDescription d)
    {
        ref var look = ref player.Get<MouseLookComponent>();
        (look.Yaw, look.Pitch) = (d.Yaw, d.Pitch);
        ref var t = ref player.Get<Transform>();
        t.Position = PhysicsConv.ToSilk(d.Position);
        t.Rotation = look.BodyRotation;
        player.Get<FreeFlyController>().MoveSpeed = d.FlySpeed;
        Players.SetFreeFlying(player, d.FreeFly); // landing puts the capsule where the Transform now is
        if (!player.Has<CharacterControllerComponent>()) return;
        ref var cc = ref player.Get<CharacterControllerComponent>();
        cc.Character.TeleportTo(d.Position); // (both no-ops while flying: there's no capsule)
        cc.Character.SetVelocity(d.Velocity);
    }

    public static PlayerDescription Describe(Entity player)
    {
        ref readonly var p = ref player.Get<Player>();
        ref readonly var look = ref player.Get<MouseLookComponent>();
        bool freeFly = player.Has<FreeFlying>();
        var velocity = !freeFly && player.Has<CharacterControllerComponent>()
            ? player.Get<CharacterControllerComponent>().Character.LinearVelocity : Vector3.Zero;
        return new PlayerDescription
        {
            Id = p.Id,
            Name = p.Name,
            FreeFly = freeFly,
            FlySpeed = player.Get<FreeFlyController>().MoveSpeed,
            Position = PhysicsConv.ToBepu(player.Get<Transform>().Position),
            Velocity = velocity,
            Yaw = look.Yaw,
            Pitch = look.Pitch,
        }.StandingOn(freeFly || !player.Has<Support>() ? default : player.Get<Support>().Supporter, PhysicsConv.ToBepu(player.Get<Transform>().Position));
    }

    /// <summary>Where a player on <paramref name="support"/> (if it's a networked entity) is on it: their world
    /// <paramref name="position"/> in its Transform's space.</summary>
    private static PlayerDescription StandingOn(this PlayerDescription d, Entity support, Vector3 position)
    {
        if (!support.IsAlive || !support.Has<EntityId>() || !support.Has<Transform>()) return d;
        ref readonly var st = ref support.Get<Transform>();
        var rotation = PhysicsConv.ToBepu(st.Rotation);
        d.Support = support.Get<EntityId>();
        d.LocalPosition = Vector3.Transform(position - PhysicsConv.ToBepu(st.Position), Quaternion.Conjugate(rotation));
        return d;
    }

    /// <summary>Where a described player is now: on their support as it is here, if it's here, else where they were
    /// saved or sent.</summary>
    public static Vector3 WorldPosition(PlayerDescription d, EntityRegistry registry)
    {
        if (d.Support.IsNone || !registry.TryGet(d.Support, out var support) || !support.Has<Transform>()) return d.Position;
        ref readonly var st = ref support.Get<Transform>();
        return PhysicsConv.ToBepu(st.Position) + Vector3.Transform(d.LocalPosition, PhysicsConv.ToBepu(st.Rotation));
    }
}
