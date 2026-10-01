using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.Persistence;

/// <summary>
/// Every stored entity's ID, kind and position, read from the save at startup and kept up to date as entities are
/// stored. An entry whose ID is already live is simply skipped when loading, so nothing loads twice and nothing needs
/// removing when an entity loads. A linear scan: fine for thousands of entities; past that it can move to a spatial
/// grid without changing anything else.
/// </summary>
public sealed class StoredEntityIndex
{
    private readonly Dictionary<EntityId, StoredEntity> _entries = new();

    public StoredEntityIndex(IEnumerable<StoredEntity> entries)
    {
        foreach (var e in entries) _entries[e.Id] = e;
    }

    public int Count => _entries.Count;
    public IEnumerable<StoredEntity> Entries => _entries.Values;
    public void Set(StoredEntity entry) => _entries[entry.Id] = entry;
    public void Remove(EntityId id) => _entries.Remove(id);
    public bool TryGet(EntityId id, out StoredEntity entry) => _entries.TryGetValue(id, out entry);
}

/// <summary>
/// Host only, each tick: loads stored entities that come within a player's load window, and unloads live ones outside
/// every window. Entities within <see cref="LoadWindow"/> blocks of any player are live and simulated; one unloads
/// past <see cref="UnloadWindow"/>, so one near the edge doesn't flicker. A stored entity with no position (a global
/// entity) is always loaded.
///
/// Loading spawns the entity from its stored description, owned by its kind's default (the host). Unloading saves it
/// (<see cref="WorldSaver.Save"/>), then despawns it; this runs before the command system, so it's gone by the end of
/// the tick.
/// </summary>
public sealed class EntityStreamingSystem : ISystem, IDebugUiSystem
{
    public const float LoadWindow = 1000f;
    public const float UnloadWindow = 1100f;
    private const int MaxLoadsPerTick = 4;

    private readonly SaveDatabase _db;
    private readonly StoredEntityIndex _index;
    private readonly EntityRegistry _registry;
    private readonly CommandSystem _commands;
    private readonly WorldSaver _saver;
    private readonly EntitySet _players;
    private readonly EntitySet _streamed;
    private readonly List<Vector3> _playerPositions = new();
    private readonly List<Entity> _leaving = new();
    private int _loads, _unloads;

    public EntityStreamingSystem(World world, SaveDatabase db, StoredEntityIndex index, EntityRegistry registry,
                                 CommandSystem commands, WorldSaver saver)
    {
        _db = db;
        _index = index;
        _registry = registry;
        _commands = commands;
        _saver = saver;
        _players = world.GetEntities().With<Player>().With<Transform>().AsSet();
        // What streams: networked entities that position themselves and aren't players (grids today).
        _streamed = world.GetEntities().With<EntityId>().With<OwnPresence>().With<Transform>().Without<Player>().Without<Chunk>().AsSet();
    }

    public void Update(float dt)
    {
        _playerPositions.Clear();
        foreach (ref readonly var p in _players.GetEntities())
        {
            var t = p.Get<Transform>().Position;
            _playerPositions.Add(new Vector3(t.X, t.Y, t.Z));
        }
        if (_playerPositions.Count == 0) return; // nobody to load around (yet)

        // Load what came into a window.
        int loads = 0;
        foreach (var entry in _index.Entries)
        {
            if (loads >= MaxLoadsPerTick) break;
            if (_registry.IsLive(entry.Id)) continue;
            if (entry.Position is { } pos && NearestPlayer(pos) > LoadWindow) continue;
            // The index and the entities table change together (WorldSaver writes and forgets both), so the row is there.
            var row = _db.ReadEntity(entry.Id) ?? throw new InvalidOperationException($"Stored entity {entry.Id} has no row in the save.");
            _commands.Spawn(row.Kind, entry.Id, row.Data);
            loads++;
            _loads++;
        }

        // Unload what left every window: until dynamic ownership every loaded entity is the host's already; with it, the
        // host takes ownership here.
        _leaving.Clear();
        foreach (ref readonly var e in _streamed.GetEntities())
            if (!IsGlobal(_index, e.Get<EntityId>()) && NearestPlayer(Where(e)) > UnloadWindow) _leaving.Add(e);
        if (_leaving.Count == 0) return;
        _saver.Save(_leaving);
        foreach (var e in _leaving) _commands.Send(new DespawnEntity { Entity = e.Get<EntityId>(), KeepStored = true });
        _unloads += _leaving.Count;
    }

    /// <summary>Where an entity is, for the windows: its body if it has one (a grid's centre of mass, which may be well
    /// away from its block origin), else its Transform.</summary>
    internal static Vector3 Where(Entity e)
    {
        ref readonly var t = ref e.Get<Transform>();
        return e.Has<PhysicsBodyComponent>() ? e.Get<PhysicsBodyComponent>().BodyPosition(t) : new Vector3(t.Position.X, t.Position.Y, t.Position.Z);
    }

    /// <summary>Stored with no position: always loaded, so never unloaded either, and it stays global when saved.</summary>
    internal static bool IsGlobal(StoredEntityIndex index, EntityId id) => index.TryGet(id, out var entry) && entry.Position is null;

    private float NearestPlayer(Vector3 pos)
    {
        float best = float.MaxValue;
        foreach (var p in _playerPositions) best = MathF.Min(best, Vector3.Distance(p, pos));
        return best;
    }

    public string DebugName => "Entity streaming";

    public void DrawDebugUi()
    {
        ImGui.Text($"Stored entities: {_index.Count}   Live: {_streamed.Count}");
        ImGui.Text($"Loaded {_loads}, unloaded {_unloads} this session (load within {LoadWindow:0}, unload past {UnloadWindow:0})");
    }
}

/// <summary>
/// Writes entities to the save: each one's description (see <see cref="CommandSystem.Describe(IEnumerable{Entity})"/>)
/// goes into the entities table (players into the players table), all in one transaction. Autosaves every
/// <see cref="AutosaveSeconds"/>, and on exit: every live entity and player, with the world's settings and every
/// edited terrain chunk.
/// </summary>
public sealed class WorldSaver : ISystem, IDebugUiSystem
{
    public const float AutosaveSeconds = 300f;

    private readonly SaveDatabase _db;
    private readonly StoredEntityIndex _index;
    private readonly CommandSystem _commands;
    private readonly EntityIdAllocator _ids;
    private readonly EntitySet _saveable;
    private float _sinceSave;
    private DateTime _lastSave;

    public WorldSaver(World world, SaveDatabase db, StoredEntityIndex index, CommandSystem commands, EntityIdAllocator ids)
    {
        _db = db;
        _index = index;
        _commands = commands;
        _ids = ids;
        _saveable = world.GetEntities().With<EntityId>().With<OwnPresence>().Without<Chunk>().AsSet();
        commands.Applied += (handler, _, evt) =>
        {
            // Despawned for good (deleted, broken up) rather than unloaded: it leaves the save too.
            if (evt is DespawnEntity { KeepStored: false } d) Forget(d.Entity);
        };
    }

    /// <summary>Called during a full save's transaction, to write edited terrain chunks.</summary>
    public Action? SaveChunks { get; set; }

    public void Update(float dt)
    {
        _sinceSave += dt;
        if (_sinceSave >= AutosaveSeconds) SaveAll();
    }

    /// <summary>Saves <paramref name="entities"/> as they are now (unloading, a player leaving), in one transaction.</summary>
    public void Save(IEnumerable<Entity> entities)
    {
        var described = _commands.Describe(entities);
        _db.InTransaction(() => Write(described));
    }

    /// <summary>Saves every live entity and player, and the edited terrain, in one transaction (autosave, exit).</summary>
    public void SaveAll()
    {
        _sinceSave = 0;
        var described = _commands.Describe(_saveable.GetEntities().ToArray());
        _db.InTransaction(() =>
        {
            Write(described);
            SaveChunks?.Invoke();
        });
        _lastSave = DateTime.Now;
        Console.WriteLine($"[save] saved {described.Count} entities and players");
    }

    private void Write(List<EntityDescription> described)
    {
        foreach (var d in described)
        {
            if (d.Entity.Has<Player>())
            {
                ref readonly var p = ref d.Entity.Get<Player>();
                _db.WritePlayer(p.Id, p.Name, d.Data);
            }
            else
            {
                Vector3? pos = d.Entity.Has<Transform>() && !EntityStreamingSystem.IsGlobal(_index, d.Id)
                    ? EntityStreamingSystem.Where(d.Entity) : null;
                _db.WriteEntity(d.Id, d.Kind, pos, d.Data);
                _index.Set(new StoredEntity(d.Id, d.Kind, pos));
            }
        }
        _db.NextFreeId = _ids.NextFree;
    }

    /// <summary>A despawned entity that was never meant to come back (deleted, or its grid broken up) leaves the save.</summary>
    public void Forget(EntityId id)
    {
        _db.DeleteEntity(id);
        _index.Remove(id);
    }

    public string DebugName => "Save";

    public void DrawDebugUi()
    {
        ImGui.Text(_lastSave == default ? "Not saved yet this session" : $"Last saved at {_lastSave:T}");
        ImGui.Text($"Autosave in {System.Math.Max(0, AutosaveSeconds - _sinceSave):0} s");
        if (ImGui.Button("Save now")) SaveAll();
    }
}
