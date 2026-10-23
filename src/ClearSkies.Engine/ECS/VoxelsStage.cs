using System.Numerics;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>A chunk's blocks as the <see cref="VoxelsStage"/> made them (null if it came out air), with their packing
/// for the GPU, and once added to its volume, that it's there.</summary>
public sealed class ChunkVoxels(ChunkKey key, ChunkData? data, ChunkPreparation? prepared)
{
    public readonly ChunkKey Key = key;
    public ChunkData? Data = data;
    public readonly ChunkPreparation? Prepared = prepared;
    /// <summary>Added to its volume (which then owns <see cref="Data"/>); made again since (<see cref="Replaced"/>).</summary>
    public bool InVolume, Replaced;
}

/// <summary>A stage whose output is its grids' chunks' blocks (<see cref="ChunkVoxels"/>): what an edit changes, and what
/// the stages after read.</summary>
public interface IBlocksStage;

/// <summary>
/// Makes a chunk's blocks at its grid's resolution, on a worker: grid 0 is full detail, loaded from the store if edited
/// (see <see cref="ChunkEdits"/>), else generated; grid k is 2^k blocks a cell, made from the full chunks under it,
/// shrunk a level at a time (<see cref="CoarseTerrain"/>). Whoever streams the chunks puts them where they go
/// (<see cref="Added"/>) and takes them out again (<see cref="Released"/>).
/// </summary>
public sealed class VoxelsStage(IReadOnlyCollection<PipelineGrid> grids, Func<IWorldGenerator> generatorFactory, IChunkStore store,
                                ChunkEdits edits, IChunkPreparer? preparer = null) : WorkerStage<ChunkVoxels>, IBlocksStage
{
    private readonly ThreadLocal<IWorldGenerator> _generator = new(generatorFactory);
    private readonly ThreadLocal<ChunkData> _scratch = new(() => new ChunkData());

    /// <summary>A chunk made, on the main thread, with what was made before it (to take out, if it was put in).</summary>
    public Action<ChunkVoxels, ChunkVoxels?>? Added;

    /// <summary>A chunk the pipeline let go of (replaced, or dropped): to take out if it was put in. Without it, its
    /// blocks are released.</summary>
    public Action<ChunkVoxels>? Released;

    /// <summary>This thread's generator.</summary>
    public IWorldGenerator Generator => _generator.Value!;

    public override string Name => "Voxels";
    public override bool AppliesTo(PipelineGrid grid) => grids.Contains(grid);
    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => Array.Empty<Pipeline.Input>();

    public override ChunkVoxels Make(ChunkKey key, StageInputs inputs)
    {
        try
        {
            var data = key.Grid == 0 ? MakeFull(key.Pos) : MakeCoarse(key.Grid, key.Pos);
            if (data == null) return new ChunkVoxels(key, null, null);
            data.IsDirty = false;
            return new ChunkVoxels(key, data, preparer?.Prepare(data));
        }
        catch (Exception e)
        {
            Console.WriteLine($"[load] {key} failed: {e}");
            return new ChunkVoxels(key, null, null);
        }
    }

    public override Change Applied(PipelineChunk record, ChunkVoxels made, ChunkVoxels? before)
    {
        Added?.Invoke(made, before);
        // A first one comes where nothing was, so it can only bury a neighbour if it's all opaque itself.
        return before == null ? Change.All with { Opacity = false } : Change.All;
    }

    public override void Release(object output)
    {
        var made = (ChunkVoxels)output;
        if (Released != null) Released(made);
        else if (!made.InVolume) made.Data?.Release();
    }

    /// <summary>Full chunk <paramref name="pos"/>: from the store if edited, else generated. Null if air.</summary>
    private ChunkData? MakeFull(ChunkPosition pos)
    {
        bool edited = edits.IsEdited(pos);
        var data = _scratch.Value!;
        if (!(edited && store.TryLoad(pos, data)))
            Generator.Generate(data, pos);
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

    /// <summary>Level <paramref name="k"/>'s chunk <paramref name="pos"/>, from the full chunks under it. Null if air.</summary>
    private ChunkData? MakeCoarse(int k, ChunkPosition pos)
    {
        int n = 1 << k;
        ulong under = edits.FullLayersUnder(k, pos.Y);
        var coarse = new Dictionary<ChunkPosition, ChunkData>();
        try
        {
            for (int j = 0; j < n * n; j++)
            {
                int x = pos.X * n + j % n, z = pos.Z * n + j / n;
                ulong edited = edits.EditedLayers(x, z);
                ulong bits = (Generator.ColumnLayers(x, z, edits.MinY) | edits.BuiltLayers(x, z) | edited) & under;
                for (; bits != 0; bits &= bits - 1)
                {
                    int layer = BitOperations.TrailingZeroCount(bits);
                    var at = new ChunkPosition(x, edits.MinY + layer, z);
                    var full = new ChunkData();
                    if (!((edited >> layer & 1) != 0 && store.TryLoad(at, full)))
                        Generator.Generate(full, at);
                    if (full.HasAnyNonAir()) CoarseTerrain.ShrinkInto(coarse, at, full, majority: true, plants: true);
                    full.Release();
                }
            }
            for (int step = 2; step <= k; step++)
            {
                var finer = coarse;
                coarse = new Dictionary<ChunkPosition, ChunkData>();
                foreach (var (p, chunk) in finer)
                {
                    if (chunk.HasAnyNonAir()) CoarseTerrain.ShrinkInto(coarse, p, chunk, majority: true, plants: true);
                    chunk.Release();
                }
            }
            if (!coarse.Remove(pos, out var made)) return null;
            made.Compact();
            if (made.HasAnyNonAir()) return made;
            made.Release();
            return null;
        }
        finally
        {
            foreach (var (_, chunk) in coarse) chunk.Release();
        }
    }
}
