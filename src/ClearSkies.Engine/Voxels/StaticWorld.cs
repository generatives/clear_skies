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

    public StaticWorld(World world) : base(world)
    {
    }

    public BlockId GetBlockWorld(int wx, int wy, int wz) => GetBlock(wx, wy, wz);
    public void    SetBlockWorld(int wx, int wy, int wz, BlockId id) => SetBlock(wx, wy, wz, id);
}
