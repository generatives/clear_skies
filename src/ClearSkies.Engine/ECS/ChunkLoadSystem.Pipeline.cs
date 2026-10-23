using ClearSkies.Engine.Core;
using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Where streamed chunks are made: the chunk pipeline (see <see cref="ChunkPipeline"/>), a grid per level and a Voxels
/// stage that makes a chunk at its level's resolution and adds it to the level's volume. Streaming asks it for the
/// chunks it wants (<see cref="Want"/>) and lets them go (<see cref="LetGoOf"/>); a chunk let go leaves its volume
/// when the pipeline drops it.
/// </summary>
public sealed partial class ChunkLoadSystem
{
    /// <summary>Levels at most, full detail included: a pipeline grid each, made up front.</summary>
    private const int MaxLevels = 8;

    private ChunkPipeline _pipeline = null!;
    private PipelineGrid[] _grids = null!;
    private VoxelsStage _voxels = null!;

    private void StartPipeline()
    {
        _pipeline = new ChunkPipeline { Priority = Distance };
        _grids = new PipelineGrid[MaxLevels];
        for (int k = 0; k < MaxLevels; k++)
            _grids[k] = _pipeline.AddGrid(k == 0 ? "full detail" : $"level {k}", k == 0 ? null : _grids[k - 1]);
        _voxels = _pipeline.Add(new VoxelsStage(this));
    }

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
    /// nothing's making it. One the pipeline didn't make (an edit did) is unloaded now.</summary>
    private void LetGoOf(TerrainLevel level, ChunkPosition p)
    {
        if (!level.Wanting.Remove(p)) { Unload(level, p); return; }
        RemoveInFlight(level, p);
        _pipeline.Want(level.Key(p), false);
    }

    /// <summary>Lets go of a removed level's chunk: the level unloads its volume itself, so what the pipeline made is
    /// no longer in it, and is made again if the level comes back.</summary>
    private void Forget(TerrainLevel level, ChunkPosition p)
    {
        var key = level.Key(p);
        if (_pipeline.Get(key)?.Output<MadeChunk>(_voxels) is { InVolume: true } made)
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

    /// <summary>How far a chunk is from its level's nearest interest, across (blocks): the pipeline makes nearest first.</summary>
    private float Distance(ChunkKey key)
    {
        if (key.Grid >= _levels.Count) return float.PositiveInfinity;
        var level = _levels[key.Grid];
        long best = long.MaxValue;
        foreach (var ring in level.Rings) best = System.Math.Min(best, Sq(key.Pos.X - ring.Centre.x) + Sq(key.Pos.Z - ring.Centre.z));
        return best == long.MaxValue ? float.PositiveInfinity : MathF.Sqrt(best) * level.Blocks;
    }

    /// <summary>A chunk made: its blocks (null if it came out air) and their packing for the GPU, and once added to its
    /// level's volume, that it's there.</summary>
    private sealed class MadeChunk(ChunkPosition pos, ChunkData? data, ChunkPreparation? prepared)
    {
        public readonly ChunkPosition Pos = pos;
        public ChunkData? Data = data;
        public readonly ChunkPreparation? Prepared = prepared;
        public TerrainLevel? Level;
        public bool InVolume, Replaced;
    }

    /// <summary>Makes a chunk at its level's resolution: in full detail, loaded from the store if edited, else
    /// generated; in a coarse level, shrunk from the full chunks under it. Then adds it to its level's volume.</summary>
    private sealed class VoxelsStage(ChunkLoadSystem load) : WorkerStage<MadeChunk>
    {
        public override string Name => "Voxels";
        public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => Array.Empty<Pipeline.Input>();

        public override MadeChunk Make(ChunkKey key, StageInputs inputs)
        {
            try
            {
                var data = key.Grid == 0 ? load.MakeFull(key.Pos) : load.MakeCoarse(key.Grid, key.Pos);
                if (data == null) return new MadeChunk(key.Pos, null, null);
                data.IsDirty = false;
                return new MadeChunk(key.Pos, data, load._preparer?.Prepare(data)); // off the main thread
            }
            catch (Exception e)
            {
                Console.WriteLine($"[load] {key} failed: {e}");
                return new MadeChunk(key.Pos, null, null);
            }
        }

        public override Change Applied(PipelineChunk record, MadeChunk made, MadeChunk? before)
        {
            load.Add(record.Key.Grid, made, before);
            return Change.All;
        }

        public override void Release(object output) => load.Release((MadeChunk)output);
    }

    /// <summary>Full chunk <paramref name="pos"/>, on a worker: from the store if edited, else generated. Null if air.</summary>
    private ChunkData? MakeFull(ChunkPosition pos)
    {
        bool edited = _edited.ContainsKey(pos);
        var data = _scratch.Value!;
        if (!(edited && _chunkStore.TryLoad(pos, data)))
            _generator.Value!.Generate(data, pos);
        data.Compact(); // stone inside an island, or sky, keeps one block instead of 64 KB
        if (data.HasAnyNonAir())
        {
            _scratch.Value = new ChunkData();
            return data;
        }
        // Generation only ever writes blocks, so an empty result leaves the buffer all air and ready to reuse; a loaded
        // edit may have overwritten more than blocks, so start that one afresh.
        if (edited) _scratch.Value = new ChunkData();
        return null;
    }

    /// <summary>Adds a chunk made for level <paramref name="k"/> to its volume, in place of what was made before; one
    /// that came out air is remembered as air.</summary>
    private void Add(int k, MadeChunk made, MadeChunk? before)
    {
        if (before != null)
        {
            before.Replaced = true;
            if (before.InVolume && before.Level is { Removed: false } was) Unload(was, before.Pos); // made again: stale
            before.InVolume = false;
            before.Data = null;
        }
        if (k >= _levels.Count) return; // its level went: released when the pipeline drops it
        var level = _levels[k];
        var pos = made.Pos;
        made.Level = level;
        RemoveInFlight(level, pos);
        if (level.Level == 0 && !level.InFlight.ContainsKey((pos.X, pos.Z))) Loaded((pos.X, pos.Z));

        if (!level.Wanting.Contains(pos)) // let go of while it was made: dropped at the end of the frame
        {
            made.Data?.Release();
            made.Data = null;
            _pipeline.Invalidate(level.Key(pos), _voxels); // made again if it's wanted again first
            return;
        }
        if (made.Data == null)
        {
            if (level.Level == 0 && _edited.ContainsKey(pos)) RecordBuild(pos, hasBlocks: false); // a build that was emptied out
            else if (level.Columns.TryGetValue((pos.X, pos.Z), out var col))
                level.Columns[(pos.X, pos.Z)] = (col.Generated, col.Air | level.Bit(pos));
            return;
        }
        if (level.Volume.IsLoaded(pos)) // an edit made it meanwhile
        {
            made.Data.Release();
            made.Data = null;
            return;
        }
        level.Volume.AddChunk(pos, made.Data, made.Prepared);
        made.InVolume = true;
        if (level.Level > 0 && CanDrop(made.Data)) level.ToDrop.Add(pos);
    }

    /// <summary>A made chunk the pipeline let go of: replaced (already out of the volume), or dropped, when it leaves
    /// the volume (with one an edit made where it was air).</summary>
    private void Release(MadeChunk made)
    {
        var level = made.Level;
        if (!made.Replaced && level is { Removed: false }
            && (made.InVolume || (level.Volume.IsLoaded(made.Pos) && !level.Wanting.Contains(made.Pos))))
        {
            Unload(level, made.Pos); // releases its blocks with it
            made.InVolume = false;
            made.Data = null;
            return;
        }
        if (!made.InVolume) made.Data?.Release();
        made.Data = null;
    }
}
