using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;

namespace ClearSkies.Engine.Physics;

/// <summary>
/// Owns the BepuPhysics2 <see cref="Simulation"/> and its <see cref="BufferPool"/>, and exposes a
/// small façade for the rest of the engine: stepping, dynamic body creation, pose readback, and
/// per-box static terrain colliders. All public coordinates use System.Numerics (the Bepu domain);
/// callers convert via <see cref="PhysicsConv"/>.
/// </summary>
public sealed class PhysicsWorld : IDisposable
{
    public Simulation Simulation { get; }
    private readonly BufferPool _pool = new();

    public PhysicsWorld(Vector3 gravity)
    {
        Simulation = Simulation.Create(
            _pool,
            new VoxelNarrowPhaseCallbacks(new SpringSettings(30, 1)),
            new VoxelPoseCallbacks(gravity, linearDamping: 0.03f, angularDamping: 0.03f),
            new SolveDescription(velocityIterationCount: 8, substepCount: 1));
    }

    public void Step(float dt) => Simulation.Timestep(dt);

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

    /// <summary>Zeroes a body's linear and angular velocity (and keeps it awake).</summary>
    public void StopBody(BodyHandle handle)
    {
        var body = Simulation.Bodies[handle];
        body.Velocity.Linear  = Vector3.Zero;
        body.Velocity.Angular = Vector3.Zero;
        body.Awake = true;
    }

    // ── Dynamic compounds (voxel grids) ──────────────────────────────────────────

    // Tracks the children buffer for each compound shape so it can be torn down on rebuild/removal.
    private readonly Dictionary<uint, Buffer<CompoundChild>> _compoundChildren = new();

    /// <summary>
    /// Builds a dynamic compound from boxes given in the grid's local space (centre + size). Returns the
    /// shape index, its computed inertia, and the centre of mass in local space. The children are
    /// recentered around the CoM by Bepu, so render offsets must subtract the same CoM.
    /// </summary>
    public (TypedIndex shape, BodyInertia inertia, Vector3 centerOfMass) BuildDynamicCompound(
        IReadOnlyList<(Vector3 center, Vector3 size)> boxes)
    {
        using var builder = new CompoundBuilder(_pool, Simulation.Shapes, boxes.Count);
        foreach (var (center, size) in boxes)
        {
            float weight = size.X * size.Y * size.Z; // uniform density
            builder.Add(new Box(size.X, size.Y, size.Z), new RigidPose(center), weight);
        }
        builder.BuildDynamicCompound(out var children, out var inertia, out var centerOfMass);

        var shape = Simulation.Shapes.Add(new Compound(children));
        _compoundChildren[shape.Packed] = children;
        return (shape, inertia, centerOfMass);
    }

    public BodyHandle AddDynamicBody(TypedIndex shape, BodyInertia inertia, Vector3 position)
        => Simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(position), inertia, new CollidableDescription(shape, 0.1f), new BodyActivityDescription(0.01f)));

    public TypedIndex GetBodyShape(BodyHandle handle) => Simulation.Bodies[handle].Collidable.Shape;

    public void SetBodyShape(BodyHandle handle, TypedIndex shape, BodyInertia inertia)
    {
        var body = Simulation.Bodies[handle];
        body.SetShape(shape);
        body.LocalInertia = inertia;
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

    // ── Static terrain colliders (one Box static per merged box) ──────────────────

    /// <summary>Adds one static box per entry of <paramref name="boxes"/> (local centre + size),
    /// offset by <paramref name="origin"/>, appending their handles to <paramref name="outHandles"/>.</summary>
    public void AddStaticBoxes(IReadOnlyList<(Vector3 center, Vector3 size)> boxes, Vector3 origin, List<StaticHandle> outHandles)
    {
        foreach (var (center, size) in boxes)
        {
            var shapeIndex = Simulation.Shapes.Add(new Box(size.X, size.Y, size.Z));
            var handle = Simulation.Statics.Add(new StaticDescription(origin + center, shapeIndex));
            outHandles.Add(handle);
        }
    }

    /// <summary>Removes the given statics and their (convex, dispose-free) shapes, then clears the list.</summary>
    public void RemoveStatics(List<StaticHandle> handles)
    {
        foreach (var h in handles)
        {
            var shapeIndex = Simulation.Statics[h].Shape;
            Simulation.Statics.Remove(h);
            Simulation.Shapes.Remove(shapeIndex);
        }
        handles.Clear();
    }

    public void Dispose()
    {
        Simulation.Dispose();
        _pool.Clear();
    }
}

/// <summary>Material + filtering rules. Permissive: any pair involving a dynamic body collides.</summary>
internal struct VoxelNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public SpringSettings ContactSpringiness;
    public float MaximumRecoveryVelocity;
    public float FrictionCoefficient;

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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
        => a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;

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
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold)
        => true;

    public void Dispose() { }
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
