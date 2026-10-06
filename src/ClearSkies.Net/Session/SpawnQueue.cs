using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Net.Protocol;
using DefaultEcs;
using Silk.NET.Maths;
using EngineSession = ClearSkies.Engine.Entities.Session;

namespace ClearSkies.Net.Session;

/// <summary>
/// The spawns a Participant has been sent but can't apply yet, each held with every event for its entity until it
/// can (one per entity, so one waiting on its terrain holds nothing else up). An entity spawns once what it needs is
/// here:
/// <list type="bullet">
/// <item>a player standing on a ship: the ship (with its body, where they'll be simulated);</item>
/// <item>one simulated here (everything, on the authority; its own player, which it predicts, on any other): the
/// terrain around it, with colliders, loaded meanwhile by a stand-in interest (all of it drawn for its own player,
/// colliders only for anything else).</item>
/// </list>
/// Its own player also waits for <see cref="LocalReady"/>: on a client, for its clock to settle on the Host's.
/// A copy that's only drawn here needs nothing but its ship. Nothing waits for ever: a ship that never comes is
/// forgotten after <see cref="SupportWaitTicks"/> (they spawn where the Host last had them), and terrain after
/// <see cref="GiveUpTicks"/>.
/// </summary>
public sealed class SpawnQueue
{
    public const int SupportWaitTicks = 600;
    public const int GiveUpTicks = 1800;

    /// <summary>A spawn waiting: its Description, and the events for it that came meanwhile.</summary>
    public sealed class Pending
    {
        public required EntityId Id;
        public required ushort Kind;
        public required PeerId Owner;
        public required uint EventNumber;
        public required Vector3 Position;
        public required byte[] Data;
        public bool Simulated;
        public bool Local;
        public PlayerDescription? Player;
        public Entity Anchor;
        public int Ticks;
        public readonly List<(EventMeta Meta, ushort Handler, byte[] Payload)> Events = new();
    }

    private readonly World _world;
    private readonly EntityRegistry _registry;
    private readonly CommandSystem _commands;
    private readonly EngineSession _session;
    private readonly Func<Vector3, bool> _terrainReady;
    private readonly List<Pending> _pending = new();

    /// <param name="terrainReady">Whether the terrain around a point has loaded here, with colliders.</param>
    public SpawnQueue(World world, EntityRegistry registry, CommandSystem commands, EngineSession session, Func<Vector3, bool> terrainReady)
    {
        _world = world;
        _registry = registry;
        _commands = commands;
        _session = session;
        _terrainReady = terrainReady;
    }

    /// <summary>Whether this machine's own player may spawn yet, besides what it needs (see above).</summary>
    public Func<bool> LocalReady { get; set; } = () => true;

    public int Count => _pending.Count;
    public IReadOnlyList<Pending> All => _pending;

    /// <summary>Raised as each spawn is applied (handed to the command system, which creates it this tick).</summary>
    public event Action<EntityId>? Spawning;

    private bool IsAuthority => _session.LocalPeer == PeerId.Host;

    public Pending? Find(EntityId id) => _pending.Find(p => p.Id == id);

    public void Add(in SpawnMessage m)
    {
        var p = new Pending { Id = m.Id, Kind = m.Kind, Owner = m.Owner, EventNumber = m.EventNumber, Position = m.Position, Data = m.Data.ToArray() };
        if (m.Kind == CommandIds.SpawnPlayer)
        {
            p.Player = DescriptionBytes.Read<PlayerDescription>(p.Data);
            p.Local = m.Owner == _session.LocalPeer;
        }
        p.Simulated = IsAuthority || p.Local;
        if (p.Simulated)
        {
            p.Anchor = _world.CreateEntity();
            p.Anchor.Set(new Transform { Position = new Vector3D<float>(m.Position.X, m.Position.Y, m.Position.Z), Rotation = Quaternion<float>.Identity, Scale = Vector3D<float>.One });
            p.Anchor.Set(new TerrainInterest { ColliderRadius = EntityPresenceSystem.ColliderRange, DrawRadius = p.Local ? 1000 : 0 });
        }
        _pending.Add(p);
    }

    /// <summary>Holds an event for a spawn still waiting; false if its entity isn't waiting here.</summary>
    public bool Hold(in EventMeta meta, ushort handler, ReadOnlySpan<byte> payload)
    {
        if (Find(meta.Target) is not { } p) return false;
        p.Events.Add((meta, handler, payload.ToArray()));
        return true;
    }

    /// <summary>Stops waiting for <paramref name="id"/> (and drops its events); the spawn that was waiting, if any.</summary>
    public Pending? Cancel(EntityId id)
    {
        if (Find(id) is not { } p) return null;
        _pending.Remove(p);
        if (p.Anchor.IsAlive) p.Anchor.Dispose();
        return p;
    }

    /// <summary>Once a tick: applies each spawn that's ready, then the events it held.</summary>
    public void Update()
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            p.Ticks++;
            if (!Ready(p)) continue;
            _pending.RemoveAt(i--);
            if (p.Anchor.IsAlive) p.Anchor.Dispose(); // it streams around itself from now on
            Apply(p);
        }
    }

    private bool Ready(Pending p)
    {
        if (p.Local && !LocalReady()) return false;
        var position = p.Position;
        if (p.Player is { FreeFly: false } d && !d.Support.IsNone)
        {
            if (!_registry.TryGet(d.Support, out var ship))
            {
                if (p.Ticks < SupportWaitTicks) return false;
                Console.WriteLine($"[net] {d.Name}'s ship ({d.Support}) never came: spawning where they were");
                d.Support = EntityId.None;
                d.Position = p.Position;
                p.Data = DescriptionBytes.Of(d);
            }
            else
            {
                if (p.Simulated && !ship.Has<PhysicsBodyComponent>() && p.Ticks < GiveUpTicks) return false;
                position = PlayerFactory.WorldPosition(d, _registry);
            }
        }
        if (!p.Simulated) return true;
        if (p.Ticks > GiveUpTicks)
        {
            Console.WriteLine($"[net] {p.Id}: the terrain around it hasn't loaded after {GiveUpTicks / 60} s, spawning anyway");
            return true;
        }
        return _terrainReady(position);
    }

    /// <summary>On the authority, as its own command (it decides it, and numbers its events on from the Description's);
    /// anywhere else, as the authority's event, numbered as the Description is, then the events held for it.</summary>
    private void Apply(Pending p)
    {
        Spawning?.Invoke(p.Id);
        if (IsAuthority)
        {
            _commands.ContinueEventNumbers(p.Id, p.EventNumber);
            _commands.Spawn(p.Kind, p.Id, p.Data, p.Owner);
            return;
        }
        var meta = new EventMeta(PeerId.Host, 0, PeerId.Host, p.Id, p.EventNumber, _commands.Tick);
        _commands.ReceiveEvent(meta, p.Kind, _commands.SpawnCommand(p.Kind, p.Id, p.Owner, p.Data));
        foreach (var (m, handler, payload) in p.Events) _commands.ReceiveEvent(m, handler, payload);
    }
}
