using ClearSkies.Engine.Core;
using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// A drawn chunk's light, as pipeline stages after its blocks and mesh, all serving <see cref="Purpose.Draw"/>: its
/// occupancy goes in the GPU store, which gives its bricks light storage (<see cref="LightStoreStage"/>); it's lit
/// (<see cref="FirstLightStage"/>); its light maps are baked once, in view or not (<see cref="FirstBakeStage"/>); it's
/// shown, dissolving in, and swaps with the finer chunks under it or the coarser one over it (<see cref="ShownStage"/>,
/// with a coarse chunk covered in the store under the finer level, <see cref="CoverStage"/>); and a coarse chunk's blocks
/// are dropped once it's meshed and in the store (<see cref="DropBlocksStage"/>). Light that changes after is baked as the chunk's seen (see
/// <see cref="ChunkRenderSystem"/>).
/// </summary>
public static class LightStages
{
    /// <summary>Adds the stages to the pipeline, after <see cref="MeshStages"/> (before it wants any chunk). The terrain
    /// says what's in range, and known air, for a coarse chunk's swap with the finer chunks under it.</summary>
    public static void Add(
        ChunkPipelineSystem system, GpuResidencySystem residency, GpuLightSystem light, Renderer renderer, UploadStage upload,
        Time time, ChunkLoadSystem terrain)
    {
        var pipeline = system.Pipeline;
        var store = pipeline.Add(new LightStoreStage(pipeline, residency));
        pipeline.Add(new CoverStage(pipeline, store, terrain, residency.SetCovered));
        var firstLight = pipeline.Add(new FirstLightStage(pipeline, light, store));
        var firstBake = pipeline.Add(new FirstBakeStage(pipeline, renderer, upload, firstLight));
        pipeline.Add(new ShownStage(pipeline, time, firstBake, terrain) { IsLit = light.IsLit, ShowFirst = light.ShowFirst });
        pipeline.Add(new DropBlocksStage(pipeline, upload, store));
    }
}

/// <summary>Puts a drawn chunk's occupancy in the GPU store (see <see cref="GpuResidencySystem.Upload"/>), again when
/// its blocks change; its output is the chunk, taken out of the store when it's let go of (no longer drawn).</summary>
public sealed class LightStoreStage(ChunkPipeline pipeline, GpuResidencySystem residency) : FrameStage
{
    public override string Name => "Light store";
    public override Purpose Serves => Purpose.Draw;

    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid)
        => IBlocksStage.Of(pipeline, grid).Select(blocks => new Pipeline.Input(blocks, Relation.Self)).ToArray();

    public override StageResult Run(PipelineChunk record, StageInputs inputs)
    {
        if (pipeline.EntryOf(record.Key) is not { } entry) return StageResult.Done(); // air
        if (!entry.DataDropped) residency.Upload(entry); // one dropped keeps what it put in
        return StageResult.Done(entry);
    }

    public override void Release(object output) => residency.Hide((ChunkEntry)output);
}

/// <summary>Waits for a chunk in the store to be lit (see <see cref="GpuLightSystem.IsLit"/>): it, and the chunks above
/// and below it that its top and bottom faces read. Asked from the frame after it's in the store, by when its bricks
/// are marked for light. A coarse chunk's are lit ahead of light changes on terrain already shown
/// (<see cref="GpuLightSystem.ShowFirst"/>): it's left out till then.</summary>
public sealed class FirstLightStage(ChunkPipeline pipeline, GpuLightSystem light, LightStoreStage store) : ExternalStage
{
    public override string Name => "First light";
    public override Purpose Serves => Purpose.Draw;

    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => new Pipeline.Input[]
    {
        new(store, Relation.Self),
        new(store, Relation.Above | Relation.Below, AcceptsMissing: true),
    };

    public override StageResult Request(PipelineChunk record)
    {
        if (pipeline.EntryOf(record.Key) is not { } entry) return StageResult.Done();
        var g = entry.Volume.Gpu;
        var p = record.Key.Pos;
        ChunkPosition above = new(p.X, p.Y + 1, p.Z), below = new(p.X, p.Y - 1, p.Z);
        if (light.IsLit(g, p) && light.IsLit(g, above) && light.IsLit(g, below)) return StageResult.Done();
        if (record.Grid.Level > 0)
        {
            light.ShowFirst(g, p);
            light.ShowFirst(g, above);
            light.ShowFirst(g, below);
        }
        return StageResult.Waiting;
    }
}

/// <summary>A chunk whose light maps are to be baked whether or not it's in view (see <see cref="FirstBakeStage"/>).</summary>
public struct BakeFirst;

/// <summary>Has a lit chunk's light maps baked once, in view or not: it's marked <see cref="BakeFirst"/>, which
/// <see cref="ChunkRenderSystem"/> bakes nearest first with what's left of its budget, until each of its meshes has
/// been baked. Again for each new mesh.</summary>
public sealed class FirstBakeStage(ChunkPipeline pipeline, Renderer renderer, UploadStage upload, FirstLightStage firstLight)
    : ExternalStage
{
    public override string Name => "First bake";
    public override Purpose Serves => Purpose.Draw;

    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid)
        => new Pipeline.Input[] { new(upload, Relation.Self), new(firstLight, Relation.Self) };

    public override StageResult Request(PipelineChunk record)
    {
        if (pipeline.EntryOf(record.Key) is not { } entry) return StageResult.Done();
        var e = entry.Entity;
        if (!e.Has<ChunkRenderData>() || Baked(in e.Get<ChunkRenderData>()))
        {
            e.Remove<BakeFirst>();
            return StageResult.Done();
        }
        e.Set<BakeFirst>();
        return StageResult.Waiting;
    }

    private bool Baked(in ChunkRenderData rd)
        => rd.IsEmpty || renderer.LightmapEverBaked(rd.Mesh) && renderer.LightmapEverBaked(rd.CutoutMesh)
                         && renderer.LightmapEverBaked(rd.TransparentMesh);
}

/// <summary>Drops a coarse chunk's blocks once it's meshed and in the GPU store: it's never edited or collided with. A
/// chunk all one block, or of opaque cubes, keeps them: they're small, or say it's buried. So does one with see-through
/// blocks on a side, which meshes again when a neighbour comes.</summary>
public sealed class DropBlocksStage(ChunkPipeline pipeline, UploadStage upload, LightStoreStage store) : FrameStage
{
    public override string Name => "Drop coarse blocks";
    public override Purpose Serves => Purpose.Draw; // an undrawn chunk is meshed when it's drawn, from its blocks

    public override bool AppliesTo(PipelineGrid grid) => grid.Level > 0;

    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid)
        => new Pipeline.Input[] { new(upload, Relation.Self), new(store, Relation.Self) };

    public override StageResult Run(PipelineChunk record, StageInputs inputs)
    {
        if (pipeline.EntryOf(record.Key) is { DataDropped: false } entry && entry.Entity.Has<ChunkMeshedFlag>() && CanDrop(entry.Data))
            entry.DropData();
        return StageResult.Done();
    }

    private static bool CanDrop(ChunkData data)
    {
        if (data.IsUniform(out _) || data.IsAllOpaque) return false;
        for (int f = 0; f < 6; f++)
            if (ChunkVolume.FaceHasTransparent(data, f)) return false;
        return true;
    }
}
