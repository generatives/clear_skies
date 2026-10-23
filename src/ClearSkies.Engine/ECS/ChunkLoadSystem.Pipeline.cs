using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Where streamed chunks are made: the chunk pipeline (see <see cref="ChunkPipeline"/>, run by
/// <see cref="ChunkPipelineSystem"/>), a grid per level, and the <see cref="VoxelsStage"/>, which makes a chunk at its
/// level's resolution; streaming adds it to the level's volume. Streaming asks the pipeline for the chunks it wants
/// (<see cref="Want"/>) and lets them go (<see cref="LetGoOf"/>); a chunk let go leaves its volume when the pipeline
/// drops it.
/// </summary>
public sealed partial class ChunkLoadSystem
{
    /// <summary>Levels at most, full detail included: a pipeline grid each, made up front.</summary>
    private const int MaxLevels = 8;

    private const float CoarseAfter = 1e7f; // past any full-detail chunk's distance

    private readonly ChunkPipeline _pipeline;
    private readonly PipelineGrid[] _grids = new PipelineGrid[MaxLevels];
    private readonly VoxelsStage _voxels;

    private readonly IDisposable _chunkAdded;
    private bool _adding; // a chunk the pipeline made going into its volume

    /// <summary>The stage that makes streamed chunks' blocks, for the stages after it to read.</summary>
    public VoxelsStage Voxels => _voxels;

    /// <summary>Asks the pipeline for the level's chunk <paramref name="p"/>.</summary>
    private void Want(TerrainLevel level, ChunkPosition p)
    {
        if (!level.Wanting.Add(p)) return;
        var key = level.Key(p);
        // Let go of and wanted again before it was dropped: it still has what it made.
        if (_pipeline.Get(key) is not { } record || record.State(_voxels) != SlotState.Done) AddInFlight(level, p);
        _pipeline.Want(key, true);
    }

    /// <summary>Lets go of the level's chunk <paramref name="p"/>: the pipeline drops it (and it leaves the volume) once
    /// nothing's making or holding it. One the pipeline didn't make (an edit did) is unloaded now.</summary>
    private void LetGoOf(TerrainLevel level, ChunkPosition p)
    {
        if (level.Wanting.Remove(p))
        {
            RemoveInFlight(level, p);
            _pipeline.Want(level.Key(p), false);
        }
        else if (_pipeline.Get(level.Key(p)) == null) Unload(level, p);
    }

    /// <summary>Lets go of a removed level's chunk: the level unloads its volume itself, so what the pipeline made is
    /// no longer in it, and is made again if the level comes back.</summary>
    private void Forget(TerrainLevel level, ChunkPosition p)
    {
        var key = level.Key(p);
        if (_pipeline.Get(key)?.Output<ChunkVoxels>(_voxels) is { InVolume: true } made)
        {
            made.InVolume = false;
            made.Data = null;
        }
        RemoveInFlight(level, p);
        _pipeline.Invalidate(key, _voxels);
        _pipeline.Want(key, false);
    }

    private void AddInFlight(TerrainLevel level, ChunkPosition p)
    {
        var col = (p.X, p.Z);
        var (chunks, layers) = level.InFlight.GetValueOrDefault(col);
        level.InFlight[col] = (chunks + 1, layers | level.Bit(p));
        level.InFlightChunks++;
    }

    private void RemoveInFlight(TerrainLevel level, ChunkPosition p)
    {
        var col = (p.X, p.Z);
        if (!level.InFlight.TryGetValue(col, out var job) || (job.Layers & level.Bit(p)) == 0) return;
        if (job.Chunks == 1) level.InFlight.Remove(col);
        else level.InFlight[col] = (job.Chunks - 1, job.Layers & ~level.Bit(p));
        level.InFlightChunks--;
    }

    /// <summary>The order the pipeline makes chunks in: nearest first (by how far they are from their level's nearest
    /// interest, across, in blocks), except that full detail's blocks are all made before any coarse chunk's.</summary>
    private float Distance(ChunkKey key, Stage stage)
    {
        if (!_voxels.AppliesTo(_pipeline.Grid(key.Grid))) return 0f; // a ship's: small, and edited where people are
        if (key.Grid >= _levels.Count) return float.PositiveInfinity;
        var level = _levels[key.Grid];
        long best = long.MaxValue;
        foreach (var ring in level.Rings) best = System.Math.Min(best, Sq(key.Pos.X - ring.Centre.x) + Sq(key.Pos.Z - ring.Centre.z));
        if (best == long.MaxValue) return float.PositiveInfinity;
        return MathF.Sqrt(best) * level.Blocks + (key.Grid == 0 || stage != _voxels ? 0f : CoarseAfter);
    }

    /// <summary>A chunk an edit made in the world: the pipeline takes it as what it made there (now, if it made air;
    /// once it's made it, if it wasn't asked for, as the sky usually isn't), so it's meshed, and its edits reach its
    /// neighbours, like the rest.</summary>
    private void ChunkAdded(in Entity e, in Chunk chunk)
    {
        if (_adding || chunk.Entry.Volume != _staticVolume) return;
        var full = FullDetail;
        var pos = chunk.Entry.Position;
        if (!full.Wanting.Contains(pos))
        {
            if (Wanted(full, pos)) Want(full, pos); // adopted as it's added (see Add)
            return;
        }
        if (_pipeline.Get(full.Key(pos))?.Output<ChunkVoxels>(_voxels) is { Data: null, InVolume: false, Replaced: false } made)
            Adopt(made, chunk.Entry);
    }

    private void Adopt(ChunkVoxels made, ChunkEntry entry)
    {
        made.Data = null; // the volume's now
        made.InVolume = true;
        entry.Entity.Set(new InPipeline { Key = made.Key });
    }

    /// <summary>The level a chunk was made for, if it's still there.</summary>
    private TerrainLevel? LevelOf(ChunkVoxels made) => made.Key.Grid < _levels.Count ? _levels[made.Key.Grid] : null;

    /// <summary>Adds a chunk made for its level to the level's volume, in place of what was made before; one that came
    /// out air is remembered as air.</summary>
    private void Add(ChunkVoxels made, ChunkVoxels? before)
    {
        if (before != null)
        {
            before.Replaced = true;
            if (before.InVolume && LevelOf(before) is { } was) Unload(was, before.Key.Pos); // made again: stale
            before.InVolume = false;
            before.Data = null;
        }
        if (LevelOf(made) is not { } level) return; // its level went: released when the pipeline drops it
        var pos = made.Key.Pos;
        RemoveInFlight(level, pos);

        if (!level.Wanting.Contains(pos)) // let go of while it was made: dropped at the end of the frame
        {
            made.Data?.Release();
            made.Data = null;
            _pipeline.Invalidate(level.Key(pos), _voxels); // made again if it's wanted again first
            return;
        }
        if (level.Volume.GetEntry(pos) is { } edited) // an edit made it meanwhile: that's what it is
        {
            made.Data?.Release();
            Adopt(made, edited);
            return;
        }
        if (made.Data == null)
        {
            if (level.Level == 0 && _edits.IsEdited(pos)) RecordBuild(pos, hasBlocks: false); // a build that was emptied out
            else if (level.Columns.TryGetValue((pos.X, pos.Z), out var col))
                level.Columns[(pos.X, pos.Z)] = (col.Generated, col.Air | level.Bit(pos));
            return;
        }
        _adding = true;
        var entry = level.Volume.AddChunk(pos, made.Data, made.Prepared);
        _adding = false;
        Adopt(made, entry);
    }

    /// <summary>A made chunk the pipeline let go of: replaced (already out of the volume), or dropped, when it leaves
    /// the volume (with one an edit made where it was air).</summary>
    private void Release(ChunkVoxels made)
    {
        var level = LevelOf(made);
        if (!made.Replaced && level != null
            && (made.InVolume || (level.Volume.IsLoaded(made.Key.Pos) && !level.Wanting.Contains(made.Key.Pos))))
        {
            Unload(level, made.Key.Pos); // releases its blocks with it
            made.InVolume = false;
            made.Data = null;
            return;
        }
        if (!made.InVolume) made.Data?.Release();
        made.Data = null;
    }
}
