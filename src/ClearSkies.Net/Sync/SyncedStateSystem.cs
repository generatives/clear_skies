using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Protocol;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Net.Sync;

/// <summary>A ship's synced block entity state gathered since its last snapshot, for <see cref="BodySync"/> to send
/// (and clear) with it.</summary>
public struct PendingSyncedState
{
    public List<SyncedValue> Values;
}

/// <summary>
/// Syncs block entity state for animation (see <see cref="SyncedFields"/>), each tick after remote bodies are posed:
/// <list type="bullet">
/// <item>Ships flown here: each registered field that changed since it was last sent goes into the ship's
/// <see cref="PendingSyncedState"/>, which <see cref="BodySync"/> sends with its next snapshot. Each ship's fields are
/// all sent again every <see cref="RefreshTicks"/>, at a tick spread by its ID (for lost changes and newcomers,
/// without every ship refreshing in the same frame).</item>
/// <item>Ships flown elsewhere: the state carried by each buffered snapshot is applied, in tick order, once the ship's
/// playback (<see cref="RemoteBodySystem.SampleTick"/>, about 10 ticks behind) passes it, so a Fan's flames start as
/// the drawn ship starts to move rather than ahead of it. A snapshot arriving after playback has passed its tick has
/// its state dropped: applying it would put older values over newer ones, and the next refresh restores anything it
/// carried. <see cref="SnapshotBuffer.Capacity"/> (32 snapshots, 64 ticks) is far above the delay, so snapshots are
/// applied before they're trimmed.</item>
/// </list>
/// </summary>
public sealed class SyncedStateSystem : ISystem, IDebugUiSystem
{
    /// <summary>At most this many values wait for one ship's snapshot; the rest go with the next.</summary>
    public const int MaxValuesPerSnapshot = 100;

    /// <summary>Every this many ticks (about a second), each ship flown here sends all its synced fields.</summary>
    public const int RefreshTicks = 60;

    private readonly SyncedFields _fields;
    private readonly RemoteBodySystem _remoteBodies;
    private readonly ITickClock _clock;
    private readonly EntitySet _ships;
    private readonly EntitySet _remoteShips;
    private readonly EntitySet _blockEntities;
    private readonly Dictionary<ChunkVolume, List<Entity>> _blocksByVolume = new();
    // Per ship flown here, each field's value as last sent (since its last refresh). A ship this machine starts flying
    // (an ownership hand-off) has none, so its first snapshot carries everything: keep it so, and never seed this
    // from received values, or a hand-off could leave the old owner's stale values in place.
    private readonly Dictionary<Entity, Dictionary<(Entity Block, byte Field), byte>> _sent = new();
    private readonly HashSet<Entity> _flownHere = new();
    private long _valuesSent, _valuesApplied, _cellsSkipped;

    public SyncedStateSystem(World world, SyncedFields fields, RemoteBodySystem remoteBodies, ITickClock clock)
    {
        _fields = fields;
        _remoteBodies = remoteBodies;
        _clock = clock;
        _ships = world.GetEntities().With<EntityId>().With<NetOwner>().With<ChunkGrid>().With<PhysicsBodyComponent>().AsSet();
        _remoteShips = world.GetEntities().With<RemoteBody>().With<ChunkGrid>().AsSet();
        _blockEntities = world.GetEntities().With<BlockRef>().AsSet();
    }

    public void Update(float dt)
    {
        Gather();
        Apply();
    }

    private void Gather()
    {
        _flownHere.Clear();
        foreach (ref readonly var e in _ships.GetEntities())
        {
            if (e.Get<NetOwner>().IsLocal) _flownHere.Add(e);
            else if (e.Has<PendingSyncedState>()) e.Get<PendingSyncedState>().Values?.Clear(); // no longer ours to send
        }
        foreach (var ship in _sent.Keys)
            if (!_flownHere.Contains(ship)) _sent.Remove(ship); // if it comes back, everything goes again
        if (_flownHere.Count == 0) return;

        GroupBlockEntities();
        foreach (var ship in _flownHere)
        {
            if (!_sent.TryGetValue(ship, out var sent)) _sent[ship] = sent = new();
            if ((_clock.Tick + ship.Get<EntityId>().Value) % RefreshTicks == 0) sent.Clear();
            if (!ship.Has<PendingSyncedState>()) ship.Set(new PendingSyncedState { Values = new List<SyncedValue>() });
            var pending = ship.Get<PendingSyncedState>().Values;
            if (_blocksByVolume.TryGetValue(ship.Get<ChunkGrid>().Volume, out var blocks))
            {
                int before = pending.Count;
                _cellsSkipped += CollectChanged(_fields, blocks, sent, pending, MaxValuesPerSnapshot);
                _valuesSent += pending.Count - before;
            }
        }
    }

    /// <summary>The block entities of the ships flown here, by volume.</summary>
    private void GroupBlockEntities()
    {
        foreach (var list in _blocksByVolume.Values) list.Clear();
        foreach (var ship in _flownHere)
        {
            var volume = ship.Get<ChunkGrid>().Volume;
            if (!_blocksByVolume.ContainsKey(volume)) _blocksByVolume[volume] = new List<Entity>();
        }
        foreach (ref readonly var e in _blockEntities.GetEntities())
            if (_blocksByVolume.TryGetValue(e.Get<BlockRef>().Volume, out var list)) list.Add(e);
        foreach (var (volume, list) in _blocksByVolume)
            if (list.Count == 0) _blocksByVolume.Remove(volume);
    }

    /// <summary>Adds to <paramref name="into"/> each of <paramref name="blocks"/>' fields whose value isn't the one in
    /// <paramref name="sent"/>, recording it there, until <paramref name="into"/> holds <paramref name="max"/> (the
    /// rest stay unsent, for next time). Returns how many blocks were skipped for having a cell out of a short's
    /// range (never, on a ship of any sensible size).</summary>
    public static int CollectChanged(SyncedFields fields, IReadOnlyList<Entity> blocks,
                                     Dictionary<(Entity Block, byte Field), byte> sent, List<SyncedValue> into, int max)
    {
        int skipped = 0;
        foreach (var block in blocks)
        {
            var cell = block.Get<BlockRef>().Position;
            if (!FitsShort(cell.X) || !FitsShort(cell.Y) || !FitsShort(cell.Z)) { skipped++; continue; }
            foreach (var field in fields.All)
            {
                if (!field.TryGet(block, out byte value)) continue;
                if (sent.TryGetValue((block, field.Id), out byte last) && last == value) continue;
                if (into.Count >= max) return skipped;
                sent[(block, field.Id)] = value;
                into.Add(new SyncedValue((short)cell.X, (short)cell.Y, (short)cell.Z, field.Id, value));
            }
        }
        return skipped;
    }

    private static bool FitsShort(int v) => v >= short.MinValue && v <= short.MaxValue;

    private void Apply()
    {
        foreach (ref readonly var e in _remoteShips.GetEntities())
        {
            ref var remote = ref e.Get<RemoteBody>();
            var buffer = remote.Buffer;
            if (buffer.Count == 0) continue;
            double playback = _remoteBodies.SampleTick(buffer);
            var volume = e.Get<ChunkGrid>().Volume;
            foreach (var (tick, snapshot) in buffer.Samples)
            {
                if (tick <= remote.StateApplied) continue; // applied (or passed, if it came late)
                if (tick > playback) break;
                if (snapshot.State != null) _valuesApplied += ApplyState(_fields, volume, snapshot.State);
                remote.StateApplied = tick;
            }
        }
    }

    /// <summary>Sets synced values on <paramref name="volume"/>'s block entities (those loaded here); returns how many
    /// were set.</summary>
    public static int ApplyState(SyncedFields fields, ChunkVolume volume, IEnumerable<SyncedValue> state)
    {
        int applied = 0;
        foreach (var v in state)
            if (fields.Find(v.Field) is { } field && volume.TryGetBlockEntity(v.X, v.Y, v.Z, out var block))
            {
                field.Set(block, v.Value);
                applied++;
            }
        return applied;
    }

    public string DebugName => "Synced state";

    public void DrawDebugUi()
    {
        ImGui.Text($"Fields: {_fields.All.Count}; ships flown here: {_flownHere.Count}");
        ImGui.Text($"Values sent {_valuesSent:N0}, applied {_valuesApplied:N0}");
        if (_cellsSkipped > 0) ImGui.TextColored(new System.Numerics.Vector4(1, 0.6f, 0.2f, 1), $"Blocks skipped (cell out of range): {_cellsSkipped:N0}");
    }
}
