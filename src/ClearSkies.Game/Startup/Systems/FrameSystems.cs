using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;

namespace ClearSkies.Game.Startup.Systems;

/// <summary>Once each frame, after the ticks: the camera at the local player's eye (unless something else moved it
/// first), what's drawn between the last two ticks (children follow), bodies owned elsewhere, and terrain streamed
/// around the view.</summary>
public static class FrameSystems
{
    public static void Add(GameWorld w)
    {
        var host = w.Host;
        var sim = w.Simulation;
        host.AddSystem(new EyeSystem(host.World), SystemStage.Frame);
        host.AddSystem(sim.Interpolation, SystemStage.Frame);
        host.AddSystem(sim.RemoteBodies, SystemStage.Frame); // about 100 ms behind
        host.AddSystem(sim.Hierarchy, SystemStage.Frame);
        host.AddSystem(w.ChunkLoad, SystemStage.Frame);
    }
}
