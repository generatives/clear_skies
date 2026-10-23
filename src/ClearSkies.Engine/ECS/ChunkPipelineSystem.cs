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
/// each ship a grid of its own while it's here.
/// </summary>
public sealed class ChunkPipelineSystem : ISystem, IDebugUiSystem
{
    /// <summary>Jobs given each worker at once: a few, so it has the next while the frame's results are taken (most of a
    /// first visit is sky, ruled out in microseconds), and few enough that the nearest still go first.</summary>
    private const int WorkerSlots = 8;

    /// <summary>Main-thread time per frame for taking finished work and running frame stages (ms).</summary>
    private const double MainMs = 1.5;

    public ChunkPipeline Pipeline { get; }

    /// <summary>A ship chunk's blocks, as its volume has them.</summary>
    public VolumeBlocksStage ShipBlocks { get; }

    private readonly Dictionary<ChunkVolume, PipelineGrid> _ships = new();
    private readonly IDisposable[] _subscriptions;

    public ChunkPipelineSystem(World world, ChunkPipeline? pipeline = null)
    {
        Pipeline = pipeline ?? new ChunkPipeline();
        ShipBlocks = Pipeline.Add(new VolumeBlocksStage(Pipeline));
        _subscriptions = new[]
        {
            world.Subscribe<ChunkBlocksChanged>(Edited),
            world.SubscribeComponentAdded<Chunk>(ChunkAdded),
            world.SubscribeEntityDisposed(Disposed),
        };
    }

    /// <summary>An edit wrote into a chunk in the pipeline: what reads its blocks runs again, as far as the edit
    /// concerns it.</summary>
    private void Edited(in ChunkBlocksChanged edit)
    {
        if (!edit.Chunk.IsAlive || !edit.Chunk.Has<InPipeline>()) return;
        var chunk = edit.Chunk.Get<InPipeline>();
        Pipeline.OutputChanged(chunk.Key, chunk.Blocks, new Change(edit.BorderFaces, edit.OpacityChanged));
    }

    /// <summary>A chunk came into a ship's volume: wanted, in the ship's grid (made with its first chunk).</summary>
    private void ChunkAdded(in Entity e, in Chunk chunk)
    {
        var volume = chunk.Entry.Volume;
        if (!volume.Root.Has<DynamicGrid>()) return;
        if (!_ships.TryGetValue(volume, out var grid))
            _ships[volume] = grid = Pipeline.AddGrid($"ship {volume.Root}", volume: volume);
        var key = grid.Key(chunk.Entry.Position);
        e.Set(new InPipeline { Key = key, Blocks = ShipBlocks });
        Pipeline.Want(key, true);
        Pipeline.Invalidate(key, ShipBlocks); // one that left and came back has new blocks
    }

    /// <summary>A ship's chunk left its volume (it's let go of), or the ship went (its grid goes).</summary>
    private void Disposed(in Entity e)
    {
        if (e.Has<ChunkGrid>() && _ships.Remove(e.Get<ChunkGrid>().Volume, out var grid)) Pipeline.RemoveGrid(grid);
        else if (e.Has<InPipeline>() && e.Get<InPipeline>().Blocks == ShipBlocks) Pipeline.Want(e.Get<InPipeline>().Key, false);
    }

    private readonly StepTimer _steps = new("Taking finished work and running stages") { Owner = "Chunk Pipeline" };

    public void Update(float dt)
    {
        _steps.Start();
        Pipeline.Update(new FrameBudgets(WorkerSlots, MainMs));
        _steps.Lap(0);
    }

    public string DebugName => "Chunk Pipeline";

    /// <summary>Each stage's chunks by state, each grid's chunks, and the chunk that's waited its turn longest.</summary>
    public void DrawDebugUi()
    {
        ImGui.Text($"Chunks: {Pipeline.ChunkCount:N0}   Worker jobs running: {Pipeline.WorkerJobs} ({WorkerSlots} a worker, {Core.BackgroundWork.Workers} workers)" +
                   (Pipeline.AwaitingExternal > 0 ? $"   Waiting on outside work: {Pipeline.AwaitingExternal}" : ""));
        foreach (var stage in Pipeline.Stages)
        {
            var (waiting, ready, running, done) = Pipeline.Count(stage);
            ImGui.Text($"{stage.Name}: {ready:N0} ready, {running:N0} running, {waiting:N0} waiting for inputs, {done:N0} done");
        }
        var byGrid = Pipeline.CountByGrid();
        for (int k = 0; k < byGrid.Length; k++)
            if (byGrid[k].Chunks > 0)
                ImGui.Text($"{Pipeline.Grid(k).Name}: {byGrid[k].Chunks:N0} chunks, {byGrid[k].Making:N0} being made");
        if (Pipeline.LongestReady() is var (record, longest, seconds))
            ImGui.Text($"Longest ready: {record.Grid.Name} {record.Key.Pos} {longest.Name}, {seconds:F1} s");
        else ImGui.Text("Longest ready: none");

        ImGui.Separator();
        _steps.Draw();
    }
}
