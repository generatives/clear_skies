using System;
using System.IO;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Generation;
using DefaultEcs;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// The streamed, world-anchored voxel terrain. Chunks are placed at their world origin and never
/// rotate; <see cref="ECS.ChunkLoadSystem"/> loads and unloads them around the camera. Block access
/// here is in world space, which for the static world is identical to volume-local space.
///
/// Edited chunks (<see cref="ChunkData.IsDirty"/>) are persisted to disk under Saves/World and reloaded
/// from there instead of being regenerated; chunks that were only ever procedurally generated and never
/// edited are never written to disk. See <see cref="Unload"/> (save on stream-out), <see cref="SaveAllDirty"/>
/// (periodic autosave + exit flush, driven externally), and <see cref="Load"/> (load-from-disk-if-present,
/// else generate).
/// </summary>
public sealed class StaticWorld : ChunkVolume
{
    private readonly string _savesDir;

    public StaticWorld(World world) : base(world)
    {
        _savesDir = Path.Combine(AppContext.BaseDirectory, "Saves", "World");
        Directory.CreateDirectory(_savesDir);
    }

    public BlockId GetBlockWorld(int wx, int wy, int wz) => GetBlock(wx, wy, wz);
    public void    SetBlockWorld(int wx, int wy, int wz, BlockId id) => SetBlock(wx, wy, wz, id);

    public void Load(ChunkPosition pos, IWorldGenerator generator)
    {
        if (IsLoaded(pos)) return;

        var data = new ChunkData();
        if (!StaticWorldSerializer.TryLoad(SavePath(pos), data))
            generator.Generate(data, pos);
        data.IsDirty = false;

        AddChunk(pos, data);
    }

    public void Unload(ChunkPosition pos)
    {
        var entry = GetEntry(pos);
        if (entry is null) return;

        SaveIfDirty(pos, entry);

        if (entry.Entity.IsAlive)
            entry.Entity.Dispose();

        _chunks.Remove(pos);
        MarkNeighboursDirty(pos, entry.Data);
    }

    /// <summary>Writes every currently loaded chunk with unsaved edits to disk, clearing its dirty flag.
    /// Called by the periodic autosave (see ChunkLoadSystem) and once on graceful shutdown.</summary>
    public void SaveAllDirty()
    {
        foreach (var (pos, entry) in All)
            SaveIfDirty(pos, entry);
    }

    private void SaveIfDirty(ChunkPosition pos, ChunkEntry entry)
    {
        if (!entry.Data.IsDirty) return;
        StaticWorldSerializer.Save(entry.Data, SavePath(pos));
        entry.Data.IsDirty = false;
    }

    private string SavePath(ChunkPosition pos) => Path.Combine(_savesDir, $"{pos.X}_{pos.Y}_{pos.Z}.chunk");
}
