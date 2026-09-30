using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Physics.Support;
using Silk.NET.Maths;

namespace ClearSkies.Game.Startup.Systems;

/// <summary>
/// Gameplay and physics, once per 1/60 s tick (0 or more times a frame, see TickClock): input handed to the tick,
/// movement, block actions, every command sent this tick, flight, presence layers, one physics step, and what came of
/// it. Mouse-look runs per frame before the ticks; presses are collected per frame and handed to the next tick as the
/// player's PlayerInput, which is all tick systems read. Creates the systems the other groups share too.
/// </summary>
public sealed class SimulationSystems
{
    private readonly GameWorld _w;

    public SimulationSystems(GameWorld w)
    {
        _w = w;
        var host = w.Host;
        Interpolation = new TickInterpolationSystem(host.World, host.Time);
        Hierarchy = new HierarchyTransformSystem(host.World);
        PhysicsBody = new PhysicsBodySystem(host.World, host.Physics);
        // Headless there's no input: players stand still, and nobody pilots.
        if (host.Input is { } input)
        {
            InputSample = new InputSampleSystem(host.World, input, host.Time);
            Pilot = new GridPilotSystem(host.World, input, host.Physics, w.StaticVolume, PhysicsBody, w.Commands);
        }
        BlockActions = new BlockActionSystem(host.World, w.Commands, w.EditLimits, w.Selection);
        Flight = new AirshipFlightSystem(host.World, host.Physics);
    }

    public InputSampleSystem? InputSample { get; }
    public TickInterpolationSystem Interpolation { get; }
    public HierarchyTransformSystem Hierarchy { get; }
    public PhysicsBodySystem PhysicsBody { get; }
    public GridPilotSystem? Pilot { get; }
    public BlockActionSystem BlockActions { get; }
    public AirshipFlightSystem Flight { get; }

    /// <summary>Early in the tick, after the network: hierarchy, and the frame's input handed to the tick.</summary>
    public void AddInput()
    {
        _w.Host.AddSystem(Hierarchy, SystemStage.Simulation);
        if (InputSample != null) _w.Host.AddSystem(InputSample, SystemStage.Simulation);
    }

    /// <summary>The rest of the tick, after the save's streaming.</summary>
    public void Add()
    {
        var host = _w.Host;
        var commands = _w.Commands;
        host.AddSystem(PhysicsBody, SystemStage.Simulation);

        // Character controller (ported from BepuPhysics2's own Demos/Demos/Characters — see Physics/Characters/):
        // motion goals (WASD/jump/mode toggle) must be set before the physics step so Simulation.Timestep's
        // CollisionsDetected analysis sees them this same tick.
        host.AddSystem(new PlayerMovementSystem(host.World, commands), SystemStage.Simulation);

        // Place, break, spawn and use controls (levers and wheels, whose control systems turn drags into commands as the
        // interactions are published), then apply every command sent this tick, then pose the controls from what the
        // commands set, so an arm is posed this tick where the view was turned to keep on it.
        host.AddSystem(BlockActions, SystemStage.Simulation);
        var levers = new LeverControlSystem(host.World, commands);
        var wheels = new SteeringWheelControlSystem(host.World, commands);
        host.AddSystem(commands, SystemStage.Simulation);
        host.AddSystem(levers, SystemStage.Simulation);
        host.AddSystem(wheels, SystemStage.Simulation);

        // Airship flight (velocity control law and Fan/Buoyant propulsion), before the physics step so its impulses
        // are integrated this same tick.
        host.AddSystem(Flight, SystemStage.Simulation);

        var chunkLoad = _w.ChunkLoad;
        var physicsBody = PhysicsBody;
        var staticVolume = _w.StaticVolume;
        host.AddSystem(new EntityPresenceSystem(host.World, _w.Session, staticVolume, _w.Options.ViewDistance)
        {
            // Entities are drawn as far as the terrain, but no further than the load window; a grid owned here gets a
            // body once the terrain around it has loaded with colliders, so nothing loaded from the save falls through
            // the world.
            RenderDistanceLimit = EntityStreamingSystem.LoadWindow,
            TerrainReady = p => chunkLoad.IsTerrainLoaded(new Vector3D<float>(p.X, p.Y, p.Z), 64f) &&
                                physicsBody.CollidersReady(staticVolume, p, 64f),
        }, SystemStage.Simulation); // presence layers: bodies, drawing, terrain interest and colliders

        host.AddSystem(host.Physics, SystemStage.Simulation); // one step, once bodies/impulses for this tick are in
        host.AddSystem(new PhysicsTransformSyncSystem(host.World, host.Physics), SystemStage.Simulation); // body poses -> Transform
        host.AddSystem(Hierarchy, SystemStage.Simulation); // e.g. volume Transforms -> chunk Transforms
        host.AddSystem(new SupportSystem(host.World, host.Physics), SystemStage.Simulation); // what each character stands on or rides with
        host.AddSystem(Interpolation, SystemStage.Simulation); // records this tick's poses
    }
}
