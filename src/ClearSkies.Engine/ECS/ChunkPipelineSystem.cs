using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Pipeline;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>Runs the chunk pipeline (see <see cref="ChunkPipeline"/>) each frame, after what wants chunks in it (see
/// <see cref="ChunkLoadSystem"/>), and shows where its chunks are.</summary>
public sealed class ChunkPipelineSystem : ISystem, IDebugUiSystem
{
    /// <summary>Worker jobs running at once. Well above the core count: the background workers queue the excess, and
    /// most of a first visit is sky that's ruled out in microseconds.</summary>
    private const int WorkerSlots = 64;

    /// <summary>Main-thread time per frame for taking finished work and running frame stages (ms).</summary>
    private const double MainMs = 1.5;

    public ChunkPipeline Pipeline { get; }

    public ChunkPipelineSystem(ChunkPipeline? pipeline = null) => Pipeline = pipeline ?? new ChunkPipeline();

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
        ImGui.Text($"Chunks: {Pipeline.ChunkCount:N0}   Worker jobs running: {Pipeline.WorkerJobs} of {WorkerSlots}" +
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
