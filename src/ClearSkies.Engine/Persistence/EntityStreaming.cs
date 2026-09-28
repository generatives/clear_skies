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
    private readonly Dictionary<uint, StoredEntity> _entries = new();

    public StoredEntityIndex(IEnumerable<StoredEntity> entries)
    {
        foreach (var e in entries) _entries[e.Id] = e;
    }

    public int Count => _entries.Count;
    public IEnumerable<StoredEntity> Entries => _entries.Values;
    public void Set(StoredEntity entry) => _entries[entry.Id] = entry;
    public void Remove(uint id) => _entries.Remove(id);
    public bool TryGet(uint id, out StoredEntity entry) => _entries.TryGetValue(id, out entry);
}

/// <summary>
/// Host only, each tick: loads stored entities that come within a player's load window, and unloads live ones outside
/// every window. Entities within <see cref="LoadWindow"/> blocks of any player are live and simulated; one unloads
/// past <see cref="UnloadWindow"/>, so one near the edge doesn't flicker. A stored entity with no position (a global
/// entity) is always loaded.
///
/// Loading sends the entity's stored spawn command (its owner is the host). Unloading has it described for storage;
/// once <see cref="WorldSaver"/> has written it, it's despawned.
/// </summary>
public sealed class EntityStreamingSystem : ISystem, IDebugUiSystem
{
    public const float LoadWindow = 1000f;
    public const float UnloadWindow = 1100f;
    private const int MaxLoadsPerTick = 4;

    private readonly SaveDatabase _db;
    private readonly StoredEntityIndex _index;
    private readonly NetRegistry _registry;
    private readonly CommandSystem _commands;
    private readonly EntitySet _players;
    private readonly EntitySet _streamed;
    private readonly List<Vector3> _playerPositions = new();
    private readonly HashSet<uint> _unloading = new();   // described for storage, not written yet
    private readonly HashSet<uint> _despawning = new();  // written, despawn sent
    private readonly List<Entity> _toUnload = new();
    private int _loads, _unloads;

    public EntityStreamingSystem(World world, SaveDatabase db, StoredEntityIndex index, NetRegistry registry,
                                 CommandSystem commands, WorldSaver saver)
    {
        _db = db;
        _index = index;
        _registry = registry;
        _commands = commands;
        _players = world.GetEntities().With<Player>().With<Transform>().AsSet();
        // What streams: networked entities that position themselves and aren't players (grids today).
        _streamed = world.GetEntities().With<NetId>().With<OwnPresence>().With<Transform>().Without<Player>().Without<Chunk>().AsSet();
        saver.Stored += OnStored;
    }

    public int Unloading => _unloading.Count;

    public void Update(float dt)
    {
        _playerPositions.Clear();
        foreach (ref readonly var p in _players.GetEntities())
        {
            var t = p.Get<Transform>().Position;
            _playerPositions.Add(new Vector3(t.X, t.Y, t.Z));
        }
        if (_playerPositions.Count == 0) return; // nobody to load around (yet)

        // Entities that went away (despawned, or disposed some other way) are done with.
        _despawning.RemoveWhere(id => !_registry.IsLive(id));
        _unloading.RemoveWhere(id => !_registry.IsLive(id));

        // Load what came into a window.
        int loads = 0;
        foreach (var entry in _index.Entries)
        {
            if (loads >= MaxLoadsPerTick) break;
            if (_registry.IsLive(entry.Id)) continue;
            if (entry.Position is { } pos && NearestPlayer(pos) > LoadWindow) continue;
            if (_db.ReadEntity(entry.Id) is not { } row) { _index.Remove(entry.Id); break; }
            _commands.SendSerialized(row.Kind, row.Data);
            loads++;
            _loads++;
        }

        // Unload what left every window.
        _toUnload.Clear();
        foreach (ref readonly var e in _streamed.GetEntities())
        {
            uint id = e.Get<NetId>().Value;
            if (_unloading.Contains(id) || _despawning.Contains(id)) continue;
            if (NearestPlayer(Where(e)) > UnloadWindow) _toUnload.Add(e);
        }
        foreach (var e in _toUnload)
        {
            // Until dynamic ownership every loaded entity is the host's already; with it, the host takes ownership here.
            _unloading.Add(e.Get<NetId>().Value);
            DescribeRequest.Request(e, DescribePurpose.Store);
        }
    }

    /// <summary>Where an entity is, for the windows: its body if it has one (a grid's centre of mass, which may be well
    /// away from its block origin), else its Transform.</summary>
    internal static Vector3 Where(Entity e)
    {
        ref readonly var t = ref e.Get<Transform>();
        return e.Has<PhysicsBodyComponent>() ? e.Get<PhysicsBodyComponent>().BodyPosition(t) : new Vector3(t.Position.X, t.Position.Y, t.Position.Z);
    }

    private float NearestPlayer(Vector3 pos)
    {
        float best = float.MaxValue;
        foreach (var p in _playerPositions) best = MathF.Min(best, Vector3.Distance(p, pos));
        return best;
    }

    /// <summary>An entity being unloaded has been written: now it goes.</summary>
    private void OnStored(uint id)
    {
        if (!_unloading.Remove(id)) return;
        if (!_registry.IsLive(id)) return;
        _despawning.Add(id);
        _commands.Send(new DespawnEntity { Entity = id, KeepStored = true });
        _unloads++;
    }

    public string DebugName => "Entity streaming";

    public void DrawDebugUi()
    {
        ImGui.Text($"Stored entities: {_index.Count}   Live: {_streamed.Count}   Unloading: {_unloading.Count}");
        ImGui.Text($"Loaded {_loads}, unloaded {_unloads} this session (load within {LoadWindow:0}, unload past {UnloadWindow:0})");
    }
}

/// <summary>
/// Writes descriptions to the save: every entity described for <see cref="DescribePurpose.Store"/> this tick goes into
/// the entities table (players into the players table), all in one transaction together with the world's settings
/// and, for an autosave, every edited terrain chunk. Autosaves every <see cref="AutosaveSeconds"/> and on exit.
/// </summary>
public sealed class WorldSaver : ISystem, IDebugUiSystem
{
    public const float AutosaveSeconds = 300f;

    private readonly SaveDatabase _db;
    private readonly StoredEntityIndex _index;
    private readonly CommandSystem _commands;
    private readonly NetIdAllocator _ids;
    private readonly EntitySet _saveable;
    private readonly List<Description> _pending = new();
    private bool _autosaving;
    private float _sinceSave;
    private DateTime _lastSave;

    public WorldSaver(World world, SaveDatabase db, StoredEntityIndex index, CommandSystem commands, NetIdAllocator ids)
    {
        _db = db;
        _index = index;
        _commands = commands;
        _ids = ids;
        _saveable = world.GetEntities().With<NetId>().With<OwnPresence>().Without<Chunk>().AsSet();
        commands.Descriptions.Described += OnDescribed;
        commands.DescribedAll += Commit;
        commands.Applied += (handler, _, evt) =>
        {
            // Despawned for good (deleted, broken up) rather than unloaded: it leaves the save too.
            if (evt is DespawnEntity { KeepStored: false } d) Forget(d.Entity);
        };
    }

    /// <summary>Called during an autosave's transaction, to write edited terrain chunks.</summary>
    public Action? SaveChunks { get; set; }

    /// <summary>An entity's description was written to the save (its network ID).</summary>
    public event Action<uint>? Stored;

    public void Update(float dt)
    {
        _sinceSave += dt;
        if (_sinceSave >= AutosaveSeconds) RequestAutosave();
    }

    /// <summary>Has every live entity and player described for storage this tick, and saves them with the chunks.</summary>
    public void RequestAutosave()
    {
        _sinceSave = 0;
        _autosaving = true;
        foreach (ref readonly var e in _saveable.GetEntities())
            DescribeRequest.Request(e, DescribePurpose.Store);
    }

    /// <summary>Saves everything now (on exit): outside the tick, so it describes and writes straight away.</summary>
    public void SaveNow()
    {
        RequestAutosave();
        _commands.DescribeRequested();
    }

    private void OnDescribed(Description d)
    {
        if ((d.Request.Purpose & DescribePurpose.Store) != 0) _pending.Add(d);
    }

    private void Commit()
    {
        if (_pending.Count == 0 && !_autosaving) return;
        var written = new List<uint>();
        _db.InTransaction(() =>
        {
            foreach (var d in _pending)
            {
                if (d.Entity.IsAlive && d.Entity.Has<Player>())
                {
                    ref readonly var p = ref d.Entity.Get<Player>();
                    _db.WritePlayer(p.Id, p.Name, d.Payload);
                }
                else
                {
                    Vector3? pos = d.Entity.IsAlive && d.Entity.Has<Transform>() ? EntityStreamingSystem.Where(d.Entity) : null;
                    _db.WriteEntity(d.NetId, d.HandlerId, pos, d.Payload);
                    _index.Set(new StoredEntity(d.NetId, d.HandlerId, pos));
                }
                written.Add(d.NetId);
            }
            if (_autosaving) SaveChunks?.Invoke();
            _db.NextFreeId = _ids.NextFree;
        });
        if (_autosaving)
        {
            _lastSave = DateTime.Now;
            Console.WriteLine($"[save] saved {written.Count} entities and players");
        }
        _pending.Clear();
        _autosaving = false;
        foreach (var id in written) Stored?.Invoke(id);
    }

    /// <summary>A despawned entity that was never meant to come back (deleted, or its grid broken up) leaves the save.</summary>
    public void Forget(uint id)
    {
        _db.DeleteEntity(id);
        _index.Remove(id);
    }

    public string DebugName => "Save";

    public void DrawDebugUi()
    {
        ImGui.Text(_lastSave == default ? "Not saved yet this session" : $"Last saved at {_lastSave:T}");
        ImGui.Text($"Autosave in {System.Math.Max(0, AutosaveSeconds - _sinceSave):0} s");
        if (ImGui.Button("Save now")) RequestAutosave();
    }
}

/// <summary>This machine's settings: who the player is, across sessions and worlds.</summary>
public sealed class LocalSettings
{
    public PlayerId PlayerId { get; private set; }
    public string Name { get; private set; } = "";

    /// <summary>Reads Saves/settings.txt, creating it with a new player ID the first time.</summary>
    public static LocalSettings LoadOrCreate(string path, string defaultName)
    {
        var settings = new LocalSettings { Name = defaultName };
        if (File.Exists(path))
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                var (key, value) = (line[..eq].Trim(), line[(eq + 1)..].Trim());
                if (key == "player_id" && Guid.TryParse(value, out var g)) settings.PlayerId = new PlayerId(g);
                if (key == "name" && value.Length > 0) settings.Name = value;
            }
        if (settings.PlayerId.Value == Guid.Empty)
        {
            settings.PlayerId = PlayerId.New();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllLines(path, new[] { $"player_id = {settings.PlayerId.Value:D}", $"name = {settings.Name}" });
        }
        return settings;
    }
}
