using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Streamed terrain's meshes, as pipeline stages after its blocks (<see cref="ChunkLoadSystem.Voxels"/>): each chunk is
/// meshed on a worker (<see cref="MeshStage"/>), then uploaded and put on its entity (<see cref="UploadStage"/>), with
/// <see cref="ChunkMeshSystem"/>'s own steps, which still mesh everything else (ships, chunks an edit made). The old mesh
/// stays on screen until the new one is up.
/// </summary>
public static class TerrainMeshStages
{
    /// <summary>Adds the stages to the pipeline the loader streams through (before it wants any chunk).</summary>
    public static (MeshStage Mesh, UploadStage Upload) Add(ChunkPipelineSystem system, ChunkLoadSystem load, ChunkMeshSystem meshes)
    {
        var pipeline = system.Pipeline;
        var mesh = pipeline.Add(new MeshStage(pipeline, load, meshes));
        system.OnDrawn.Add(mesh);
        var upload = pipeline.Add(new UploadStage(load, meshes, mesh));
        return (mesh, upload);
    }
}

/// <summary>
/// Meshes a chunk on a worker, from its blocks and its neighbours' as they are in the volume (held for the job).
/// Streamed terrain is meshed without its neighbours (<see cref="ChunkVolume.MeshIgnoresNeighbours"/>): one only
/// matters where this chunk has see-through blocks on the face they share, or where both are all opaque (this one is
/// buried once all six are, and has no mesh). So that's when a neighbour coming, going or being edited meshes it again.
/// </summary>
public sealed class MeshStage(ChunkPipeline pipeline, ChunkLoadSystem load, ChunkMeshSystem meshes) : WorkerStage<PackedChunkMesh>
{
    private readonly VoxelsStage _voxels = load.Voxels;

    public override string Name => "Mesh";

    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => new Pipeline.Input[]
    {
        new Pipeline.Input(_voxels, Relation.Self),
        new Pipeline.Input(_voxels, Relation.Neighbours.Where(Matters), AcceptsMissing: true),
    };

    /// <summary>Whether a change to neighbour <paramref name="source"/>'s blocks can change <paramref name="made"/>'s mesh.</summary>
    private bool Matters(ChunkKey made, ChunkKey source, Change change)
    {
        int face = Faces.Toward(source.Pos, made.Pos);
        if (!change.Touches(face) || Blocks(made) is not { } data) return false;
        if (ChunkVolume.FaceHasTransparent(data, face ^ 1)) return true;
        return data.IsAllOpaque && (change.Opacity || Blocks(source) is { IsAllOpaque: true });
    }

    /// <summary>A wanted chunk's blocks as made (null if not made yet, or air).</summary>
    private ChunkData? Blocks(ChunkKey key)
        => pipeline.Get(key) is { Wanted: true } record ? record.Output<ChunkVoxels>(_voxels)?.Data : null;

    /// <summary>What a job meshes: the chunk's blocks and its neighbours', held until it's done.</summary>
    private sealed record Job(ChunkData Data, ChunkData?[] Around, bool Alone, ChunkPosition Pos);

    /// <summary>The chunk's blocks and its neighbours', held for the job; nothing for one with nothing to draw (all air,
    /// or buried), and <see cref="PackedChunkMesh.Unchanged"/> for one it doesn't mesh: not drawn here (meshed when it
    /// is, see <see cref="ChunkPipelineSystem.OnDrawn"/>), its blocks dropped, or an edit made it.</summary>
    public override object? Prepare(PipelineChunk record)
    {
        var entry = load.EntryOf(record.Key);
        if (entry == null || entry.DataDropped || !entry.Entity.Has<Rendered>()) return PackedChunkMesh.Unchanged;
        if (!entry.Data.HasAnyNonAir()) return null;
        var volume = entry.Volume;
        var around = new ChunkData?[6];
        bool buried = entry.Data.IsAllOpaque;
        for (int f = 0; f < 6; f++)
        {
            var key = record.Key with { Pos = Faces.Across(record.Key.Pos, f) };
            // A neighbour let go of reads as missing already, though it's in the volume until it's dropped.
            if (pipeline.Get(key) is { Wanted: true } n && n.Output<ChunkVoxels>(_voxels) is { } made)
                around[f] = made.Data ?? volume.GetData(key.Pos); // air, unless an edit made it since
            buried &= around[f] is { IsAllOpaque: true };
        }
        if (buried) return null;
        entry.Data.Retain();
        foreach (var data in around) data?.Retain();
        return new Job(entry.Data, around, volume.MeshIgnoresNeighbours, entry.Position);
    }

    public override PackedChunkMesh Make(ChunkKey key, StageInputs inputs)
    {
        if (inputs.Prepared is PackedChunkMesh unchanged) return unchanged;
        if (inputs.Prepared is not Job job) return PackedChunkMesh.Nothing();
        var a = job.Around;
        try { return meshes.MeshOnWorker(job.Data, a[0], a[1], a[2], a[3], a[4], a[5], job.Alone, job.Pos); }
        finally
        {
            job.Data.Unretain();
            foreach (var data in a) data?.Unretain();
        }
    }

    public override void Release(object output) => ((PackedChunkMesh)output).ReturnBuffers();
}

/// <summary>Uploads a chunk's new mesh and puts it on the chunk's entity in place of the old one (main thread).</summary>
public sealed class UploadStage(ChunkLoadSystem load, ChunkMeshSystem meshes, MeshStage mesh) : FrameStage
{
    public override string Name => "Upload";

    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => new Pipeline.Input[] { new(mesh, Relation.Self) };

    public override StageResult Run(PipelineChunk record, StageInputs inputs)
    {
        var packed = inputs.Get<PackedChunkMesh>(mesh, record.Key)!;
        if (packed == PackedChunkMesh.Unchanged) return StageResult.Done();
        if (load.EntryOf(record.Key) is { } entry) meshes.Show(entry, packed);
        else packed.ReturnBuffers(); // gone meanwhile
        return StageResult.Done();
    }
}
