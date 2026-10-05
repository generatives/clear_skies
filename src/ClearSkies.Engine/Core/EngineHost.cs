using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Physics;
using DefaultEcs;

namespace ClearSkies.Engine.Core;

/// <summary>
/// Owns the ECS world, physics and the system schedule, and drives the update stages. On its own it has nothing that
/// needs a display: <see cref="Run"/> runs the update stages from a timer, once per tick's worth of time, until
/// <see cref="Quit"/> (a dedicated server, or a bot). <see cref="WindowedEngineHost"/> adds the window, GPU, input and
/// debug UI, and the render stages.
/// </summary>
public class EngineHost : IDisposable
{
    // Update stages hold ISystems, render stages IRenderSystems (enforced by AddSystem).
    private protected readonly List<(object system, SystemStage stage)> _systems = new();

    public World World { get; }
    public PhysicsWorld Physics { get; }
    public Time Time { get; }

    /// <summary>Decides how many fixed ticks each frame runs (see <see cref="SystemStage.Simulation"/>).</summary>
    public TickClock Clock { get; } = new();

    public EngineHost()
    {
        World = new World();
        Time = new Time();
        // Milestone 5 airships need real gravity for weight/lift to mean anything (a Buoyant block
        // counteracting nothing is meaningless). Gentler than Earth to fit the "magical steampunk sky
        // world" and give Fan/Buoyant tuning room (see AirshipFlightSystem's debug sliders).
        Physics = new PhysicsWorld(new System.Numerics.Vector3(0f, -6f, 0f), Time.FixedStep);
        PowerThrottling.OptOut(); // keep full speed when another window is in front
    }

    /// <summary>Schedules <paramref name="system"/> in an update stage (Input, Simulation, Frame or PreRender), after the
    /// systems already in it.</summary>
    public void AddSystem(ISystem system, SystemStage stage)
    {
        if (IsRenderStage(stage))
            throw new ArgumentException($"{stage} is a render stage; it takes an {nameof(IRenderSystem)}.", nameof(stage));
        Schedule(system, stage);
    }

    /// <summary>Schedules one stage of a system with work in several: call once per stage it runs in, each at the point
    /// in that stage where it should run.</summary>
    public void AddSystem(IStagedSystem system, SystemStage stage)
    {
        if (IsRenderStage(stage))
            throw new ArgumentException($"{stage} is a render stage; it takes an {nameof(IRenderSystem)}.", nameof(stage));
        if (_systems.Exists(s => s.system == system && s.stage == stage))
            throw new ArgumentException($"{system.GetType().Name} is already scheduled in {stage}.", nameof(stage));
        Schedule(system, stage);
    }

    private protected static bool IsRenderStage(SystemStage stage) => stage >= SystemStage.RenderWorld;

    private protected void Schedule(object system, SystemStage stage)
    {
        bool first = !_systems.Exists(s => s.system == system);
        _systems.Add((system, stage));
        _systemMs.Add(0.0);
        _frameMs.Add(0.0);
        _rawMs.Add(0.0);
        if (first && system is IDebugUiSystem debugUi) OnDebugUiScheduled(debugUi);
    }

    /// <summary>A system with a debug panel was scheduled (the debug UI shows it, where there is one).</summary>
    private protected virtual void OnDebugUiScheduled(IDebugUiSystem panel) { }

    // Per-system CPU time per frame (ms, smoothed), parallel to _systems; _frameMs sums this frame's runs (a Simulation
    // system runs several times in some frames and not at all in others).
    private protected readonly List<double> _systemMs = new();
    private protected readonly List<double> _frameMs = new();
    private protected readonly List<double> _rawMs = new(); // this frame's time per system, unsmoothed
    private protected readonly System.Diagnostics.Stopwatch _systemTimer = new();
    private protected const double TimingSmoothing = 0.05;

    public int SystemCount => _systems.Count;
    public string SystemName(int i) => $"{_systems[i].system.GetType().Name} ({_systems[i].stage})";

    /// <summary>A system's CPU time (ms) in the frame just ended.</summary>
    public double LastSystemMs(int i) => _rawMs[i];

    private volatile bool _quit;

    /// <summary>Runs the update stages, one frame per tick's worth of time, sleeping in between, until
    /// <see cref="Quit"/>.</summary>
    public virtual void Run()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        while (!_quit)
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = now - last;
            last = now;
            Time.Advance(dt);
            Update(dt);
            double spare = Time.FixedStep - (clock.Elapsed.TotalSeconds - now);
            if (spare > 0) Thread.Sleep(TimeSpan.FromSeconds(spare));
        }
    }

    /// <summary>Ends <see cref="Run"/> at the end of this frame.</summary>
    public virtual void Quit() => _quit = true;

    /// <summary>One frame of the update stages: Input, the ticks due (Simulation), Frame and PreRender.</summary>
    private protected void Update(double dt)
    {
        RunStage(SystemStage.Input, (float)dt);

        long dropped = Clock.DroppedTicks;
        int ticks = Clock.Advance(dt);
        if (Clock.DroppedTicks > dropped)
            Console.WriteLine($"[time] a {dt * 1000:0} ms frame: {Clock.DroppedTicks - dropped} ticks dropped (the clock falls behind)");
        for (int i = 0; i < ticks; i++)
        {
            Clock.Tick++;
            RunStage(SystemStage.Simulation, Time.TickSeconds);
        }
        Time.TicksLastFrame = ticks;
        Time.Alpha = Clock.Alpha;

        RunStage(SystemStage.Frame, (float)dt);
        RunStage(SystemStage.PreRender, (float)dt);
        SmoothTimes(render: false);
    }

    private void RunStage(SystemStage stage, float dt)
    {
        for (int i = 0; i < _systems.Count; i++)
        {
            var (system, s) = _systems[i];
            if (s != stage) continue;
            _systemTimer.Restart();
            if (system is IStagedSystem staged) staged.Update(stage, dt);
            else ((ISystem)system).Update(dt);
            RecordTime(i);
        }
    }

    private protected void RecordTime(int i) => _frameMs[i] += _systemTimer.Elapsed.TotalMilliseconds;

    /// <summary>Folds this frame's time for the update (or render) systems into their smoothed times.</summary>
    private protected void SmoothTimes(bool render)
    {
        for (int i = 0; i < _systems.Count; i++)
        {
            if (IsRenderStage(_systems[i].stage) != render) continue;
            _systemMs[i] += TimingSmoothing * (_frameMs[i] - _systemMs[i]);
            _rawMs[i] = _frameMs[i];
            _frameMs[i] = 0;
        }
    }

    public virtual void Dispose()
    {
        BackgroundWork.Stop(TimeSpan.FromSeconds(5)); // before freeing what running jobs may be using
        Physics.Dispose();
        World.Dispose();
    }
}
