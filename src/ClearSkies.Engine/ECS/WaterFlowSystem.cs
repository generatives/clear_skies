using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Runs the world's water (<see cref="WaterFlow"/>) on the machine with authority over the world, a step every
/// <see cref="StepTicks"/> ticks, and sends each step's changes as <see cref="FlowWater"/> commands, so every machine
/// sees the same water. Edits to the world wake the cells around them; anywhere else, nothing is looked at. Runs
/// before the command system in the tick, so a step's changes apply in the same tick.
/// </summary>
public sealed class WaterFlowSystem : ISystem, IDisposable
{
    /// <summary>Ticks between steps: about six a second, so water runs about six blocks a second.</summary>
    public const int StepTicks = 10;

    private readonly CommandSystem _commands;
    private int _ticks;

    public WaterFlowSystem(ChunkVolume world, CommandSystem commands)
    {
        _commands = commands;
        Flow = new WaterFlow(world);
        _commands.Applied += OnApplied;
    }

    public WaterFlow Flow { get; }

    private bool IsAuthority => _commands.Session.LocalPeer == PeerId.Host;

    public void Update(float dt)
    {
        if (!IsAuthority) { Flow.Clear(); return; }
        if (++_ticks < StepTicks) return;
        _ticks = 0;
        if (Flow.Woken == 0) return;

        var changes = Flow.Step();
        for (int i = 0; i < changes.Count; i += FlowWaterHandler.MaxChanges)
        {
            var batch = new WaterChange[System.Math.Min(FlowWaterHandler.MaxChanges, changes.Count - i)];
            for (int k = 0; k < batch.Length; k++) batch[k] = changes[i + k];
            _commands.Send(new FlowWater { Changes = batch });
        }
    }

    /// <summary>An edit to the world wakes the water around it.</summary>
    private void OnApplied(CommandHandlerBase handler, EventMeta meta, object evt)
    {
        if (!IsAuthority || evt is not EditVoxels edit || edit.Volume != EntityRegistry.WorldVolume) return;
        foreach (var op in edit.Ops) Flow.Wake(op.Min, op.Max);
    }

    public void Dispose() => _commands.Applied -= OnApplied;
}
