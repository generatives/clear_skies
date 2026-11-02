using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Runs the chunk pipeline (see <see cref="ChunkPipeline"/>) each frame, after what wants chunks in it (see
/// <see cref="ChunkLoadSystem"/>), and shows where its chunks are. It turns what happens to chunks in the ECS into
/// pipeline calls: an edit (<see cref="ChunkBlocksChanged"/>) is a change to the chunk's blocks, and a ship's chunks
/// (those of a <see cref="DynamicGrid"/>'s volume, made by edits rather than streamed) are wanted as they come and go,
/// each ship a grid of its own while it's here: for <see cref="Purpose.Collide"/> (its body is built from them), and for
/// <see cref="Purpose.Draw"/> while the chunk's entity is <see cref="Rendered"/>.
/// </summary>
public sealed class ChunkPipelineSystem : ISystem, IDebugUiSystem
{
    /// <summary>Jobs given each worker at once: a few, so it has the next while the frame's results are taken (most of a
    /// first visit is sky, ruled out in microseconds), and few enough that the nearest still go first.</summary>
    private const int WorkerSlots = 8;

    /// <summary>Main-thread time per frame for taking finished work and running frame stages (ms).</summary>
    private const double MainMs = 4.0;

    public ChunkPipeline Pipeline { get; }

    /// <summary>A ship chunk's blocks, as its volume has them.</summary>
    public VolumeBlocksStage ShipBlocks { get; }

    private readonly IDisposable[] _subscriptions;
    // Ship chunks that may have started or stopped being drawn: their entity came, went, or gained or lost the layer.
    // Each is looked at as the pipeline runs, so a chunk replaced by another in the same place is wanted for what its
    // entity is then.
    private readonly HashSet<ChunkKey> _maybeWantedFor = new();

    public ChunkPipelineSystem(World world, ChunkPipeline? pipeline = null)
    {
        Pipeline = pipeline ?? new ChunkPipeline();
        ShipBlocks = Pipeline.Add(new VolumeBlocksStage(Pipeline));
        _subscriptions = new[]
        {
            world.Subscribe<ChunkBlocksChanged>(Edited),
            world.SubscribeComponentAdded<Chunk>(ChunkAdded),
            world.SubscribeEntityDisposed(Disposed),
            world.SubscribeComponentAdded((in Entity e, in InPipeline _) => MaybeWantedFor(e)),
            world.SubscribeComponentChanged((in Entity e, in InPipeline _, in InPipeline _) => MaybeWantedFor(e)),
            world.SubscribeComponentAdded((in Entity e, in Rendered _) => MaybeWantedFor(e)),
            world.SubscribeComponentRemoved((in Entity e, in Rendered _) => MaybeWantedFor(e)),
        };
    }

    /// <summary>An edit wrote into a chunk in the pipeline: what reads its blocks runs again, as far as the edit
    /// concerns it.</summary>
    private void Edited(in ChunkBlocksChanged edit)
    {
        if (!edit.Chunk.IsAlive || !edit.Chunk.Has<InPipeline>()) return;
        var key = edit.Chunk.Get<InPipeline>().Key;
        var grid = Pipeline.Grid(key.Grid);
        foreach (var stage in IBlocksStage.Of(Pipeline, grid))
            Pipeline.OutputChanged(key, stage, new Change(edit.BorderFaces, edit.OpacityChanged, Urgent: true));
    }

    /// <summary>A chunk came into a ship's volume: wanted, in the ship's grid (made with its first chunk).</summary>
    private void ChunkAdded(in Entity e, in Chunk chunk)
    {
        var volume = chunk.Entry.Volume;
        if (!volume.Root.Has<DynamicGrid>()) return;
        var key = Pipeline.GridFor(volume, $"ship {volume.Root}").Key(chunk.Entry.Position);
        e.Set(new InPipeline { Key = key });
        Pipeline.Want(key, Purpose.Collide);
        Pipeline.Invalidate(key, ShipBlocks); // one that left and came back has new blocks
    }

    /// <summary>A ship's chunk left its volume (it's let go of), or the ship went (its grid goes).</summary>
    private void Disposed(in Entity e)
    {
        MaybeWantedFor(e);
        if (e.Has<ChunkGrid>() && Pipeline.GridOf(e.Get<ChunkGrid>().Volume) is { } grid) Pipeline.RemoveGrid(grid);
        else if (e.Has<InPipeline>() && ShipBlocks.AppliesTo(Pipeline.Grid(e.Get<InPipeline>().Key.Grid))) Pipeline.Want(e.Get<InPipeline>().Key, Purpose.None);
    }

    private void MaybeWantedFor(in Entity e)
    {
        if (e.Has<InPipeline>()) _maybeWantedFor.Add(e.Get<InPipeline>().Key);
    }

    private readonly StepTimer _steps = new("Wanting ship chunks drawn or not", "Taking finished work and running stages") { Owner = "Chunk Pipeline" };
    private StepTimer? _byStage; // the same frame's time by stage, made once the stages are all there

    public void Update(float dt)
    {
        _steps.Start();
        foreach (var key in _maybeWantedFor)
            if (Pipeline.EntryOf(key)?.Entity is { } entity && ShipBlocks.AppliesTo(Pipeline.Grid(key.Grid)))
                Pipeline.Want(key, Purpose.Collide | (entity.Has<Rendered>() ? Purpose.Draw : Purpose.None));
        _maybeWantedFor.Clear();
        _steps.Lap(0);
        var gc = GC.GetTotalPauseDuration();
        Pipeline.Update(new FrameBudgets(WorkerSlots, MainMs));
        _steps.Lap(1);
        int n = Pipeline.FrameMs.Length;
        if (_byStage?.Count != n + 2)
            _byStage = new StepTimer(Pipeline.Stages.Select(s => s.Name).Append("Dropping chunks")
                                         .Append("GC pauses (in the times above)")
                                         .Append("Pipeline bookkeeping (in the times above)").ToArray()) { Owner = "Chunk Pipeline stages" };
        _byStage.Start();
        for (int i = 0; i < n; i++) _byStage.Add(i, Pipeline.FrameMs[i]);
        _byStage.Add(n, (GC.GetTotalPauseDuration() - gc).TotalMilliseconds); // a stage's spike may be the GC's
        _byStage.Add(n + 1, Pipeline.BookkeepingMs); // outside the stages' own code: what could move off the main thread
    }

    public string DebugName => "Chunk Pipeline";

    // The panel's counts, worked out a few times a second: each walks every chunk (and the stages' counts, each stage).
    private readonly List<string> _lines = new();
    private readonly System.Diagnostics.Stopwatch _linesAge = new();
    private const double LinesEveryMs = 250;

    /// <summary>How long a chunk waits on a stage before the panel counts it stuck (seconds).</summary>
    private const double StuckSeconds = 5;

    /// <summary>Each stage's chunks by state, each grid's chunks, and the chunk that's waited its turn longest.</summary>
    public void DrawDebugUi()
    {
        if (!_linesAge.IsRunning || _linesAge.Elapsed.TotalMilliseconds >= LinesEveryMs) CountLines();
        foreach (var line in _lines) ImGui.Text(line);
        ImGui.Separator();
        _steps.Draw();
        _byStage?.Draw("By stage (ms):");
    }

    private void CountLines()
    {
        _linesAge.Restart();
        _lines.Clear();
        _lines.Add($"Chunks: {Pipeline.ChunkCount:N0}   Worker jobs running: {Pipeline.WorkerJobs} ({WorkerSlots} a worker, {Core.BackgroundWork.Workers} workers)" +
                   (Pipeline.AwaitingExternal > 0 ? $"   Waiting on outside work: {Pipeline.AwaitingExternal}" : ""));
        foreach (var stage in Pipeline.Stages)
        {
            var (waiting, ready, running, done) = Pipeline.Count(stage);
            _lines.Add($"{stage.Name}: {ready:N0} ready, {running:N0} running, {waiting:N0} waiting for inputs, {done:N0} done");
        }
        var byGrid = Pipeline.CountByGrid();
        for (int k = 0; k < byGrid.Length; k++)
            if (byGrid[k].Chunks > 0)
                _lines.Add($"{Pipeline.Grid(k).Name}: {byGrid[k].Chunks:N0} chunks, {byGrid[k].Making:N0} being made");
        var stuck = Pipeline.Stuck(StuckSeconds);
        int all = stuck.Sum(s => s.Count);
        _lines.Add(all == 0 ? $"Stuck (over {StuckSeconds:F0} s): none" : $"Stuck (over {StuckSeconds:F0} s): {all:N0}");
        for (int i = 0; i < stuck.Length; i++)
            if (stuck[i] is { Count: > 0, Oldest: { } oldest } s)
            {
                var stage = Pipeline.Stages[i];
                _lines.Add($"  {stage.Name}: {s.Count:N0}; longest {oldest.Grid.Name} {oldest.Key.Pos}, {s.Seconds:F0} s, " +
                           $"waiting on {Pipeline.WaitsOn(oldest, stage)}");
            }
    }
}
