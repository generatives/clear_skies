using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// The blocks of a ship's grid (built rather than generated): already in its volume, so there's nothing to make; done
/// as soon as the chunk is wanted, with the volume's blocks as its output (never released to the pool: they're the
/// volume's). An edit to them comes as a change to this stage's output (<see cref="ChunkPipeline.OutputChanged"/>).
/// </summary>
public sealed class VolumeBlocksStage(ChunkPipeline pipeline) : FrameStage, IBlocksStage
{
    public override string Name => "Blocks";
    public override bool AppliesTo(PipelineGrid grid) => grid.Volume?.Root is { IsAlive: true } root && root.Has<DynamicGrid>();
    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => Array.Empty<Pipeline.Input>();

    /// <summary>The chunk came where nothing was (it's run as it's added), so it can only bury a neighbour if it's all
    /// opaque itself.</summary>
    public override StageResult Run(PipelineChunk record, StageInputs inputs)
    {
        var data = pipeline.EntryOf(record.Key)?.Data;
        return StageResult.Done(new ChunkVoxels(record.Key, data, null) { InVolume = true },
                                Change.All with { Opacity = data?.IsAllOpaque == true });
    }
}
