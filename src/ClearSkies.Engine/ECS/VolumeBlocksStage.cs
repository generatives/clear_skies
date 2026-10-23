using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// The blocks of a ship's grid (built rather than generated): what's in
/// the volume now, as <see cref="ChunkVoxels"/> like the terrain's <see cref="VoxelsStage"/> makes, so the stages after
/// read either. The volume keeps the blocks; an edit to them comes as a change to this output
/// (<see cref="ChunkPipeline.OutputChanged"/>).
/// </summary>
public sealed class VolumeBlocksStage(ChunkPipeline pipeline) : FrameStage, IBlocksStage
{
    public override string Name => "Blocks";
    public override bool AppliesTo(PipelineGrid grid) => grid.Volume?.Root is { IsAlive: true } root && root.Has<DynamicGrid>();
    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => Array.Empty<Pipeline.Input>();

    public override StageResult Run(PipelineChunk record, StageInputs inputs)
    {
        var entry = pipeline.EntryOf(record.Key);
        return StageResult.Done(new ChunkVoxels(record.Key, entry?.Data, null) { InVolume = entry != null });
    }
}
