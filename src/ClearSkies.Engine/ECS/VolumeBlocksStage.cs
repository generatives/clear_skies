using ClearSkies.Engine.Pipeline;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// The blocks of a grid the pipeline doesn't generate (a ship's, see <see cref="PipelineGrid.Generated"/>): what's in
/// the volume now, as <see cref="ChunkVoxels"/> like the terrain's <see cref="VoxelsStage"/> makes, so the stages after
/// read either. The volume keeps the blocks; an edit to them comes as a change to this output
/// (<see cref="ChunkPipeline.OutputChanged"/>).
/// </summary>
public sealed class VolumeBlocksStage(ChunkPipeline pipeline) : FrameStage
{
    public override string Name => "Blocks";
    public override bool AppliesTo(PipelineGrid grid) => !grid.Generated;
    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => Array.Empty<Pipeline.Input>();

    public override StageResult Run(PipelineChunk record, StageInputs inputs)
    {
        var entry = pipeline.EntryOf(record.Key);
        return StageResult.Done(new ChunkVoxels(record.Key, entry?.Data, null) { InVolume = entry != null });
    }
}
