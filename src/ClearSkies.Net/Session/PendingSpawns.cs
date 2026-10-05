using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Net.Session;

/// <summary>
/// The authority's players waiting to spawn, each until what they need is here. A player isn't spawned (so isn't
/// simulated, or seen) until then, so nothing is left falling through a world still loading under it. Each waits at a
/// <see cref="SpawnAnchor"/>, which loads the save's entities around it (their ship, say) as a player would.
/// <list type="bullet">
/// <item>Standing on a ship: the ship is loaded (here, and so in the world a joining client is sent), with its body.</item>
/// <item>The terrain around them has loaded here, with colliders, since every player is simulated here (the anchor
/// streams it: all of it for the host's own player, colliders only for a client's).</item>
/// <item>A joining client: they've loaded their own terrain there too (<c>TerrainReady</c>), to predict themselves on.</item>
/// </list>
/// What's here can't take for ever: a ship still missing after <see cref="SupportWaitTicks"/> is forgotten (they spawn
/// where they were), and waits for this machine end after <see cref="GiveUpTicks"/>. A client is always waited for.
/// </summary>
public sealed class PendingSpawns
{
    public const int SupportWaitTicks = 600;
    public const int GiveUpTicks = 1800;

    private sealed class Pending
    {
        public required PlayerDescription Description;
        public required PeerId Owner;
        public required bool Local;
        public required Entity Anchor;
        public required Action<PlayerDescription> Spawn;
        public bool ClientReady;
        public int Ticks;
    }

    private readonly World _world;
    private readonly EntityRegistry _registry;
    private readonly Func<Vector3, bool>? _terrainReady;
    private readonly List<Pending> _pending = new();

    /// <param name="terrainReady">Whether the terrain around a point has loaded with colliders here.</param>
    public PendingSpawns(World world, EntityRegistry registry, Func<Vector3, bool>? terrainReady)
    {
        _world = world;
        _registry = registry;
        _terrainReady = terrainReady;
    }

    public int Count => _pending.Count;

    public bool IsWaiting(PlayerId player) => _pending.Exists(p => p.Description.Id == player);

    /// <summary>Waits to spawn <paramref name="description"/> for <paramref name="owner"/> (played here if
    /// <paramref name="local"/>), then calls <paramref name="spawn"/> with it, its position on its ship resolved.</summary>
    public void Add(PlayerDescription description, PeerId owner, bool local, Action<PlayerDescription> spawn)
    {
        var anchor = _world.CreateEntity();
        var p = description.Position;
        anchor.Set(new Transform { Position = new Vector3D<float>(p.X, p.Y, p.Z), Rotation = Quaternion<float>.Identity, Scale = Vector3D<float>.One });
        anchor.Set<SpawnAnchor>();
        // The terrain here, to simulate them on: drawn for the host's own player, colliders only for a client's.
        anchor.Set(new TerrainInterest { ColliderRadius = EntityPresenceSystem.ColliderRange, DrawRadius = local ? 1000 : 0 });
        _pending.Add(new Pending { Description = description, Owner = owner, Local = local, Anchor = anchor, Spawn = spawn });
    }

    /// <summary>The client <paramref name="owner"/> has loaded its terrain.</summary>
    public void ClientReady(PeerId owner)
    {
        foreach (var p in _pending)
            if (p.Owner == owner) p.ClientReady = true;
    }

    /// <summary>Stops waiting for <paramref name="owner"/> (they left).</summary>
    public void Cancel(PeerId owner)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (_pending[i].Owner != owner) continue;
            _pending[i].Anchor.Dispose();
            _pending.RemoveAt(i);
        }
    }

    /// <summary>Once a tick: spawns whoever is ready.</summary>
    public void Update()
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            p.Ticks++;
            if (!Ready(p)) continue;
            _pending.RemoveAt(i--);
            p.Anchor.Dispose(); // the player streams around itself from now on
            p.Description.Position = PlayerFactory.WorldPosition(p.Description, _registry);
            p.Spawn(p.Description);
        }
    }

    private bool Ready(Pending p)
    {
        var d = p.Description;
        bool givenUp = p.Ticks > GiveUpTicks;
        if (!d.Support.IsNone)
        {
            if (!_registry.TryGet(d.Support, out var ship))
            {
                if (p.Ticks < SupportWaitTicks) return false;
                Console.WriteLine($"[net] {d.Name}'s ship ({d.Support}) never loaded: spawning where they were");
                d.Support = EntityId.None;
            }
            else if (!ship.Has<PhysicsBodyComponent>() && !givenUp) return false;
        }
        if (!p.Local && !p.ClientReady) return false;
        if (givenUp)
        {
            Console.WriteLine($"[net] {d.Name}: the world around them hasn't loaded after {GiveUpTicks / 60} s, spawning anyway");
            return true;
        }
        return _terrainReady?.Invoke(PlayerFactory.WorldPosition(d, _registry)) ?? true;
    }
}
