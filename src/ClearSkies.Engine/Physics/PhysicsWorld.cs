using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Physics.Characters;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Physics;

/// <summary>
/// Owns the BepuPhysics2 <see cref="Simulation"/> and its <see cref="BufferPool"/>, and exposes a
/// small façade for the rest of the engine: dynamic body creation, pose readback, and per-chunk static
/// terrain colliders. All public coordinates use System.Numerics (the Bepu domain); callers convert
/// via <see cref="PhysicsConv"/>.
///
/// Doubles as the <see cref="ISystem"/> that steps the simulation: <see cref="Update"/> advances it
/// on a fixed timestep decoupled from the variable frame rate, accumulating frame delta and stepping
/// in fixed increments (capped per frame to avoid a "spiral of death" after a long stall). Register it
/// with <c>AddSystem(host.Physics, SystemStage.Logic)</c> at the point in the Logic stage where physics
/// should step — after systems that create bodies or apply impulses, before systems that read poses.
/// </summary>
public sealed class PhysicsWorld : ISystem, IDisposable, Gui.IDebugUiSystem
{
    private const int MaxStepsPerFrame = 5;

    // Debug-panel stats: fixed steps run last frame and smoothed cost of one step.
    private readonly System.Diagnostics.Stopwatch _stepTimer = new();
    private int _stepsLastFrame;
    private double _stepMs;

    public Simulation Simulation { get; }
    public Vector3 Gravity { get; }

    /// <summary>Manages capsule player/NPC characters riding on top of the simulation — see
    /// <c>Physics/Characters/</c> (ported from BepuPhysics2's own Demos/Demos/Characters, v2.4.0).
    /// Support detection and the character motion constraint hook themselves into
    /// <see cref="Simulation"/>'s narrow phase and Timestepper events once <see cref="Simulation.Create"/>
    /// calls <see cref="VoxelNarrowPhaseCallbacks.Initialize"/> below — <see cref="Update"/> needs no
    /// changes to drive it.</summary>
    public CharacterControllers Characters { get; }

    private readonly BufferPool _pool = new();
    private readonly float _fixedStep;
    private float _accumulator;

    public PhysicsWorld(Vector3 gravity, float fixedStep)
    {
        Gravity = gravity;
        _fixedStep = fixedStep;
        Characters = new CharacterControllers(_pool);
        Simulation = Simulation.Create(
            _pool,
            new VoxelNarrowPhaseCallbacks(new SpringSettings(30, 1)) { Characters = Characters },
            new VoxelPoseCallbacks(gravity, linearDamping: 0.03f, angularDamping: 0.03f),
            new SolveDescription(velocityIterationCount: 8, substepCount: 1));
    }

    public void Update(float dt)
    {
        _accumulator += dt;

        int steps = 0;
        while (_accumulator >= _fixedStep && steps < MaxStepsPerFrame)
        {
            _stepTimer.Restart();
            Simulation.Timestep(_fixedStep);
            _stepMs += 0.1 * (_stepTimer.Elapsed.TotalMilliseconds - _stepMs);
            _accumulator -= _fixedStep;
            steps++;
        }
        _stepsLastFrame = steps;

        // If we hit the cap and still have a large backlog, drop it rather than chase forever.
        if (_accumulator > _fixedStep) _accumulator = 0f;
    }

    // ── Debug UI ────────────────────────────────────────────────────────────────
    public string DebugName => "Physics";

    public void DrawDebugUi()
    {
        ImGuiNET.ImGui.Text($"Steps last frame: {_stepsLastFrame} (max {MaxStepsPerFrame}), one step: {_stepMs:F2} ms");
        ImGuiNET.ImGui.Text($"Awake bodies: {Simulation.Bodies.ActiveSet.Count}, statics: {Simulation.Statics.Count}, " +
                            $"constraints: {Simulation.Solver.CountConstraints()}");
        ref var set = ref Simulation.Bodies.ActiveSet;
        for (int i = 0; i < set.Count && i < 16; i++)
        {
            var body = Simulation.Bodies[set.IndexToHandle[i]];
            var p = body.Pose.Position;
            ImGuiNET.ImGui.Text($"  body {set.IndexToHandle[i].Value}: at ({p.X:F0}, {p.Y:F0}, {p.Z:F0}), " +
                                $"constraints {body.Constraints.Count}, kinematic {body.Kinematic}");
        }
    }

    // ── Dynamic bodies ──────────────────────────────────────────────────────────

    /// <summary>Creates a dynamic box body of the given world-space size and mass at a position.</summary>
    public BodyHandle AddDynamicBox(Vector3 position, Vector3 size, float mass)
    {
        var box = new Box(size.X, size.Y, size.Z);
        var shapeIndex = Simulation.Shapes.Add(box);
        var inertia = box.ComputeInertia(mass);
        return Simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(position),
            inertia,
            new CollidableDescription(shapeIndex, 0.1f),
            new BodyActivityDescription(0.01f)));
    }

    public (Vector3 position, Quaternion orientation) GetBodyPose(BodyHandle handle)
    {
        var pose = Simulation.Bodies[handle].Pose;
        return (pose.Position, pose.Orientation);
    }

    public void SetBodyPose(BodyHandle handle, Vector3 position, Quaternion orientation)
    {
        var body = Simulation.Bodies[handle];
        body.Pose = new RigidPose(position, orientation);
        body.Awake = true;
    }

    /// <summary>Mass of a dynamic body (0 for kinematic / infinite-mass).</summary>
    public float GetBodyMass(BodyHandle handle)
    {
        float inv = Simulation.Bodies[handle].LocalInertia.InverseMass;
        return inv > 0f ? 1f / inv : 0f;
    }

    public void ApplyLinearImpulse(BodyHandle handle, Vector3 impulse)
    {
        var body = Simulation.Bodies[handle];
        body.Awake = true;
        body.ApplyLinearImpulse(impulse);
    }

    /// <summary>Applies an impulse at a world-space offset from the body's centre of mass, inducing
    /// torque for free (torque impulse = offset × impulse, converted to an angular velocity change via
    /// the body's current world inverse inertia tensor). Used by AirshipFlightSystem so a Fan or
    /// Buoyant block's own position drives both translation and rotation.</summary>
    public void ApplyLinearImpulse(BodyHandle handle, Vector3 impulse, Vector3 worldOffsetFromCenterOfMass)
    {
        var body = Simulation.Bodies[handle];
        body.Awake = true;
        body.ApplyLinearImpulse(impulse);
        body.ApplyAngularImpulse(Vector3.Cross(worldOffsetFromCenterOfMass, impulse));
    }

    public Vector3 GetBodyLinearVelocity(BodyHandle handle)  => Simulation.Bodies[handle].Velocity.Linear;
    public Vector3 GetBodyAngularVelocity(BodyHandle handle) => Simulation.Bodies[handle].Velocity.Angular;

    /// <summary>Applies a pure torque impulse at the body's centre of mass (no linear effect). Used by
    /// the "free propulsion" debug mode to move a grid directly from its desired force/torque, without
    /// needing Fan blocks to realize it.</summary>
    public void ApplyAngularImpulse(BodyHandle handle, Vector3 angularImpulse)
    {
        var body = Simulation.Bodies[handle];
        body.Awake = true;
        body.ApplyAngularImpulse(angularImpulse);
    }

    public void SetBodyAngularVelocity(BodyHandle handle, Vector3 angularVelocity)
    {
        var body = Simulation.Bodies[handle];
        body.Velocity.Angular = angularVelocity;
        body.Awake = true;
    }

    /// <summary>Zeroes a body's linear and angular velocity (and keeps it awake).</summary>
    public void StopBody(BodyHandle handle)
    {
        var body = Simulation.Bodies[handle];
        body.Velocity.Linear  = Vector3.Zero;
        body.Velocity.Angular = Vector3.Zero;
        body.Awake = true;
    }

    /// <summary>Toggles a body between kinematic (zero inverse mass/inertia — ignores gravity and
    /// impulses, per <see cref="VoxelPoseCallbacks.IntegrateVelocityForKinematics"/>) and dynamic.
    /// Zeroes velocity either way. <paramref name="dynamicInertia"/> is only used when un-locking
    /// (<paramref name="kinematic"/> false) — pass the inertia last computed for this body's shape.</summary>
    public void SetBodyKinematic(BodyHandle handle, bool kinematic, BodyInertia dynamicInertia)
    {
        var body = Simulation.Bodies[handle];
        body.Velocity.Linear  = Vector3.Zero;
        body.Velocity.Angular = Vector3.Zero;
        body.SetLocalInertia(kinematic ? default : dynamicInertia);
        body.Awake = true;
    }

    // ── Dynamic compounds (voxel grids) ──────────────────────────────────────────

    // Tracks the children buffer for each compound shape so it can be torn down on rebuild/removal.
    private readonly Dictionary<uint, Buffer<CompoundChild>> _compoundChildren = new();

    /// <summary>
    /// Builds a dynamic compound from boxes given in the grid's local space (centre + size + mass —
    /// callers derive mass from per-block-type density; see PhysicsBodySystem). Returns the shape index,
    /// its computed inertia, and the centre of mass in local space. The children are recentered around
    /// the CoM by Bepu, so render offsets must subtract the same CoM.
    /// </summary>
    public (TypedIndex shape, BodyInertia inertia, Vector3 centerOfMass) BuildDynamicCompound(
        IReadOnlyList<(Vector3 center, Vector3 size, float mass)> boxes)
    {
        using var builder = new CompoundBuilder(_pool, Simulation.Shapes, boxes.Count);
        foreach (var (center, size, mass) in boxes)
            builder.Add(new Box(size.X, size.Y, size.Z), new RigidPose(center), mass);
        builder.BuildDynamicCompound(out var children, out var inertia, out var centerOfMass);

        var shape = Simulation.Shapes.Add(new Compound(children));
        _compoundChildren[shape.Packed] = children;
        return (shape, inertia, centerOfMass);
    }

    public BodyHandle AddDynamicBody(TypedIndex shape, BodyInertia inertia, Vector3 position)
        => Simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(position), inertia, new CollidableDescription(shape, 0.1f), new BodyActivityDescription(0.01f)));

    /// <summary>Removes a dynamic body. Callers that created it via <see cref="AddDynamicBody"/> with a
    /// compound shape should read the shape with <see cref="GetBodyShape"/> first, then pass it to
    /// <see cref="RemoveCompound"/> after this call to also free the shape.</summary>
    public void RemoveBody(BodyHandle handle) => Simulation.Bodies.Remove(handle);

    public TypedIndex GetBodyShape(BodyHandle handle) => Simulation.Bodies[handle].Collidable.Shape;

    public void SetBodyShape(BodyHandle handle, TypedIndex shape, BodyInertia inertia)
    {
        var body = Simulation.Bodies[handle];
        body.SetShape(shape);
        // SetLocalInertia, not the LocalInertia property (a raw ref-return onto the body's memory) —
        // see SetBodyKinematic's note; this path also crosses the kinematic/dynamic boundary whenever
        // a locked grid's shape is rebuilt (block edit), so it needs the same proper transition call.
        body.SetLocalInertia(inertia);
        body.Awake = true;
    }

    /// <summary>Removes a compound shape: its child convex shapes, its children buffer, then the compound itself.</summary>
    public void RemoveCompound(TypedIndex shape)
    {
        if (_compoundChildren.Remove(shape.Packed, out var children))
        {
            for (int i = 0; i < children.Length; i++)
                Simulation.Shapes.Remove(children[i].ShapeIndex);
            _pool.Return(ref children);
        }
        Simulation.Shapes.Remove(shape);
    }

    // ── Static terrain colliders (one BigCompound static per chunk) ───────────────

    /// <summary>A static terrain compound's boxes plus its prebuilt acceleration tree, serialized so it can be
    /// built off the main thread (see <see cref="PrepareStaticCompound"/>) and copied into the simulation's pool
    /// cheaply by <see cref="AddStaticCompound"/>.</summary>
    public sealed class StaticCompoundBuild
    {
        internal readonly (Vector3 center, Vector3 size)[] Boxes;
        internal readonly byte[]? Tree; // null: single box, tree built on add
        internal StaticCompoundBuild((Vector3, Vector3)[] boxes, byte[]? tree) { Boxes = boxes; Tree = tree; }
        public int BoxCount => Boxes.Length;
    }

    /// <summary>The serialized tree inside a <see cref="StaticCompoundBuild"/>, for StreamingBenchmark's check that
    /// it matches what BigCompound's own constructor builds.</summary>
    public static ReadOnlySpan<byte> DebugTreeBytes(StaticCompoundBuild build) => build.Tree ?? default(ReadOnlySpan<byte>);

    // Per-worker pool for PrepareStaticCompound's temporary tree: BufferPool isn't thread-safe, so workers
    // can't touch _pool (which the simulation uses on the main thread).
    [ThreadStatic] private static BufferPool? t_buildPool;

    /// <summary>Thread-safe first half of adding a static compound: builds the <see cref="BigCompound"/>
    /// acceleration tree over <paramref name="boxes"/> (centre + size, compound-local) the same way
    /// BigCompound's own constructor does (a sweep build, one leaf per box in order) — then serializes it. Touches no
    /// simulation state, so it can run on a worker thread.</summary>
    public static StaticCompoundBuild PrepareStaticCompound(IReadOnlyList<(Vector3 center, Vector3 size, BlockId id)> boxes)
    {
        var pool = t_buildPool ??= new BufferPool();
        var copy = new (Vector3, Vector3)[boxes.Count];
        pool.Take<BoundingBox>(boxes.Count, out var leafBounds);
        for (int i = 0; i < boxes.Count; i++)
        {
            var (center, size, _) = boxes[i];
            copy[i] = (center, size);
            var half = size * 0.5f; // an unrotated box's bounds, as BigCompound computes them at identity
            leafBounds[i] = new BoundingBox(center - half, center + half);
        }
        // A one-leaf tree doesn't survive this route (SweepBuild and the Span deserializer both leave it with no
        // root node, unlike BigCompound's constructor), so AddStaticCompound builds that trivial case itself.
        if (boxes.Count == 1)
        {
            pool.Return(ref leafBounds);
            return new StaticCompoundBuild(copy, null);
        }
        var tree = new Tree(pool, boxes.Count);
        tree.SweepBuild(pool, leafBounds);
        pool.Return(ref leafBounds);
        var bytes = new byte[tree.GetSerializedByteCount()];
        tree.Serialize(bytes);
        tree.Dispose(pool);
        return new StaticCompoundBuild(copy, bytes);
    }

    /// <summary>Adds a single static whose shape is a <see cref="BigCompound"/> of one box child per box in
    /// <paramref name="build"/> (local to <paramref name="origin"/>). One static per chunk keeps the broad phase
    /// small — the compound's own internal tree handles the per-box culling — instead of inserting every merged
    /// box as its own static. Main thread only; the tree itself was built by <see cref="PrepareStaticCompound"/>.</summary>
    public StaticHandle AddStaticCompound(StaticCompoundBuild build, Vector3 origin)
    {
        var boxes = build.Boxes;
        _pool.Take<CompoundChild>(boxes.Length, out var children);
        for (int i = 0; i < boxes.Length; i++)
        {
            var (center, size) = boxes[i];
            children[i] = new CompoundChild
            {
                LocalPose  = new RigidPose(center),
                ShapeIndex = Simulation.Shapes.Add(new Box(size.X, size.Y, size.Z)),
            };
        }
        var compound = build.Tree is null
            ? new BigCompound(children, Simulation.Shapes, _pool)
            : new BigCompound { Children = children, Tree = new Tree(build.Tree, _pool) };
        var shape = Simulation.Shapes.Add(compound);
        return Simulation.Statics.Add(new StaticDescription(origin, shape));
    }

    /// <summary>Removes a static created by <see cref="AddStaticCompound"/>, along with its compound
    /// shape, child boxes, children buffer and acceleration tree.</summary>
    public void RemoveStaticCompound(StaticHandle handle)
    {
        var shape = Simulation.Statics[handle].Shape;
        Simulation.Statics.Remove(handle);
        Simulation.Shapes.RecursivelyRemoveAndDispose(shape, _pool);
    }

    public void Dispose()
    {
        Simulation.Dispose();
        _pool.Clear();
    }
}

/// <summary>Material + filtering rules. Fully permissive — every candidate pair the broad phase hands
/// us generates contacts (it already excludes static-static pairs on its own, since neither side can
/// move). Was previously gated on "at least one side is Dynamic," written before locked (kinematic)
/// grids existed; that gate silently dropped Kinematic-vs-Static contacts.</summary>
internal struct VoxelNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public SpringSettings ContactSpringiness;
    public float MaximumRecoveryVelocity;
    public float FrictionCoefficient;

    /// <summary>Set by <see cref="PhysicsWorld"/>'s constructor. Ported from BepuPhysics2's own
    /// CharacterNarrowphaseCallbacks (see Physics/Characters/) — forwards Initialize and reports
    /// every contact manifold to it so it can detect ground support for registered characters.</summary>
    public CharacterControllers? Characters;

    public VoxelNarrowPhaseCallbacks(SpringSettings contactSpringiness, float maximumRecoveryVelocity = 2f, float frictionCoefficient = 1f)
    {
        ContactSpringiness = contactSpringiness;
        MaximumRecoveryVelocity = maximumRecoveryVelocity;
        FrictionCoefficient = frictionCoefficient;
    }

    public void Initialize(Simulation simulation)
    {
        if (ContactSpringiness.AngularFrequency == 0 && ContactSpringiness.TwiceDampingRatio == 0)
        {
            ContactSpringiness = new SpringSettings(30, 1);
            MaximumRecoveryVelocity = 2f;
            FrictionCoefficient = 1f;
        }
        Characters?.Initialize(simulation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
        => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
        => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial.FrictionCoefficient = FrictionCoefficient;
        pairMaterial.MaximumRecoveryVelocity = MaximumRecoveryVelocity;
        pairMaterial.SpringSettings = ContactSpringiness;
        // No-ops unless this pair involves a registered character, in which case it records the
        // support candidate and zeroes FrictionCoefficient (the character motion constraint takes
        // over holding it to the surface, so raw contact friction would only fight the constraint).
        Characters?.TryReportContacts(pair, ref manifold, workerIndex, ref pairMaterial);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold)
        => true;

    public void Dispose() => Characters?.Dispose();
}

/// <summary>Applies constant gravity and light damping each substep (vectorised callback).</summary>
internal struct VoxelPoseCallbacks : IPoseIntegratorCallbacks
{
    public Vector3 Gravity;
    public float LinearDamping;
    public float AngularDamping;

    private Vector3Wide _gravityDt;
    private System.Numerics.Vector<float> _linearDampingDt;
    private System.Numerics.Vector<float> _angularDampingDt;

    public VoxelPoseCallbacks(Vector3 gravity, float linearDamping = 0.03f, float angularDamping = 0.03f) : this()
    {
        Gravity = gravity;
        LinearDamping = linearDamping;
        AngularDamping = angularDamping;
    }

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    public void Initialize(Simulation simulation) { }

    public void PrepareForIntegration(float dt)
    {
        _linearDampingDt  = new System.Numerics.Vector<float>(MathF.Pow(System.Math.Clamp(1 - LinearDamping, 0, 1), dt));
        _angularDampingDt = new System.Numerics.Vector<float>(MathF.Pow(System.Math.Clamp(1 - AngularDamping, 0, 1), dt));
        _gravityDt = Vector3Wide.Broadcast(Gravity * dt);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IntegrateVelocity(
        System.Numerics.Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, System.Numerics.Vector<int> integrationMask, int workerIndex,
        System.Numerics.Vector<float> dt, ref BodyVelocityWide velocity)
    {
        velocity.Linear  = (velocity.Linear + _gravityDt) * _linearDampingDt;
        velocity.Angular = velocity.Angular * _angularDampingDt;
    }
}
