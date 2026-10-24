using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// The blocks of a ship's grid, or another volume's put there by hand (built rather than generated): already in its
/// volume, so there's nothing to make; done as soon as the chunk is wanted, so the stages after read the volume. An
/// edit to them comes as a change to this stage's output (<see cref="ChunkPipeline.OutputChanged"/>).
/// </summary>
public sealed class VolumeBlocksStage(ChunkPipeline pipeline) : FrameStage, IBlocksStage
{
    public override string Name => "Blocks";
    public override bool AppliesTo(PipelineGrid grid) => grid.ForVolume;
    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => Array.Empty<Pipeline.Input>();

    /// <summary>The chunk came where nothing was (it's run as it's added), so it can only bury a neighbour if it's all
    /// opaque itself.</summary>
    public override StageResult Run(PipelineChunk record, StageInputs inputs)
        => StageResult.Done(null, Change.All with { Opacity = pipeline.EntryOf(record.Key)?.Data.IsAllOpaque == true });
}
