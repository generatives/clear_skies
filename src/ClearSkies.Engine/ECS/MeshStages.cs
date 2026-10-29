using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Chunks' meshes, as pipeline stages after their blocks (the terrain's <see cref="VoxelsStage"/>, or a ship's
/// <see cref="VolumeBlocksStage"/>): each chunk is meshed on a worker (<see cref="MeshStage"/>), then uploaded and put on
/// its entity (<see cref="UploadStage"/>), with <see cref="ChunkMeshes"/>' steps. The old mesh stays on screen until the
/// new one is up. Both serve <see cref="Purpose.Draw"/>: a chunk not drawn here has no mesh.
/// </summary>
public static class MeshStages
{
    /// <summary>Adds the stages to the pipeline (before it wants any chunk).</summary>
    public static (MeshStage Mesh, UploadStage Upload) Add(ChunkPipelineSystem system, ChunkMeshes meshes)
    {
        var pipeline = system.Pipeline;
        var mesh = pipeline.Add(new MeshStage(pipeline, meshes));
        var upload = pipeline.Add(new UploadStage(pipeline, meshes, mesh));
        return (mesh, upload);
    }
}

/// <summary>
/// Meshes a chunk on a worker, from its blocks and its neighbours'. A ship's chunks cull their faces
/// against their neighbours, so any change on a border they share meshes them again. Streamed terrain is meshed
/// without its neighbours (<see cref="ChunkVolume.MeshIgnoresNeighbours"/>): one only matters where this chunk has
/// see-through blocks on the face they share, or where both are all opaque (this one is buried once all six are, and
/// has no mesh). So that's when a neighbour coming, going or being edited meshes it again.
/// </summary>
public sealed class MeshStage(ChunkPipeline pipeline, ChunkMeshes meshes)
    : WorkerStage<PackedChunkMesh>
{
    public override string Name => "Mesh";
    public override Purpose Serves => Purpose.Draw;

    /// <summary>The blocks of the chunk and its neighbours, from whichever stage gives the grid's blocks (generated
    /// terrain's, or a ship's).</summary>
    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid)
        => IBlocksStage.Of(pipeline, grid).SelectMany(blocks => new Pipeline.Input[]
        {
            new(blocks, Relation.Self),
            new(blocks, Relation.Neighbours.Where(Matters), AcceptsMissing: true),
        }).ToArray();

    /// <summary>Whether a change to neighbour <paramref name="source"/>'s blocks can change <paramref name="made"/>'s mesh.</summary>
    private bool Matters(ChunkKey made, ChunkKey source, Change change)
    {
        if (Blocks(made) is not { } data) return false;
        if (data.IsAllOpaque && change.Opacity) return true; // it may be buried now, or dug out
        int face = Faces.Toward(source.Pos, made.Pos);
        if (!change.Touches(face)) return false;
        if (pipeline.Grid(made.Grid).Volume is { MeshIgnoresNeighbours: false }) return true;
        return ChunkVolume.FaceHasTransparent(data, face ^ 1) || data.IsAllOpaque && Blocks(source) is { IsAllOpaque: true };
    }

    /// <summary>A wanted chunk's blocks, from its volume (null if not there yet, or air). One let go of reads as missing,
    /// though it's in the volume until it's dropped.</summary>
    private ChunkData? Blocks(ChunkKey key)
        => pipeline.Get(key) is { Wanted: true } ? pipeline.EntryOf(key)?.Data : null;

    /// <summary>What a job meshes: the chunk's blocks and its neighbours' (null where it doesn't read one).</summary>
    private sealed record Job(ChunkData Data, ChunkData?[] Around, bool Alone, ChunkPosition Pos);

    /// <summary>The chunk's blocks and its neighbours', as they are now; nothing for one with nothing to draw (all air,
    /// or buried), and <see cref="PackedChunkMesh.Unchanged"/> for one it can't mesh: its blocks dropped, or gone.</summary>
    public override object? Prepare(PipelineChunk record, StageInputs inputs)
    {
        var entry = pipeline.EntryOf(record.Key);
        if (entry == null || entry.DataDropped) return PackedChunkMesh.Unchanged;
        if (inputs.Get<ChunkVoxels>(record.Key)?.Data is not { } data || !data.HasAnyNonAir()) return null;
        var around = new ChunkData?[6];
        bool buried = data.IsAllOpaque;
        for (int f = 0; f < 6; f++)
        {
            around[f] = inputs.Get<ChunkVoxels>(record.Key with { Pos = Faces.Across(record.Key.Pos, f) })?.Data;
            buried &= around[f] is { IsAllOpaque: true };
        }
        return buried ? null : new Job(data, around, entry.Volume.MeshIgnoresNeighbours, entry.Position);
    }

    public override PackedChunkMesh Make(ChunkKey key, StageInputs inputs)
    {
        if (inputs.Prepared is PackedChunkMesh unchanged) return unchanged;
        if (inputs.Prepared is not Job job) return PackedChunkMesh.Nothing();
        var a = job.Around;
        return meshes.MeshOnWorker(job.Data, a[0], a[1], a[2], a[3], a[4], a[5], job.Alone, job.Pos);
    }

    public override void Release(object output) => ((PackedChunkMesh)output).ReturnBuffers();
}

/// <summary>Uploads a chunk's new mesh and puts it on the chunk's entity in place of the old one (main thread). Its
/// output is the chunk it's shown on, whose mesh goes when it's let go of (the chunk's no longer drawn).</summary>
public sealed class UploadStage(ChunkPipeline pipeline, ChunkMeshes meshes, MeshStage mesh) : FrameStage
{
    public override string Name => "Upload";
    public override Purpose Serves => Purpose.Draw;

    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => new Pipeline.Input[] { new(mesh, Relation.Self) };

    public override StageResult Run(PipelineChunk record, StageInputs inputs)
    {
        var packed = inputs.Get<PackedChunkMesh>(mesh, record.Key)!;
        var entry = pipeline.EntryOf(record.Key);
        if (packed == PackedChunkMesh.Unchanged) { }
        else if (entry != null) meshes.Show(entry, packed);
        else packed.ReturnBuffers(); // gone meanwhile
        return StageResult.Done(entry);
    }

    public override void Release(object output) => ChunkMeshes.Hide((ChunkEntry)output);
}
