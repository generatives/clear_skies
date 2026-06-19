using BepuPhysics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Input;
using Silk.NET.Maths;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Validation harness for Phase 3.1: pressing <c>B</c> spawns a 1³ dynamic box in front of the camera.
/// Each frame, every spawned box's entity <see cref="Transform"/> is synced from its Bepu body pose —
/// a preview of the dynamic-grid pose sync in Phase 3.2. Confirms gravity, the fixed step, and terrain
/// collision: the box should fall and rest on the ground.
/// </summary>
public sealed class DebugDropSystem : ISystem
{
    private readonly World           _world;
    private readonly PhysicsWorld    _physics;
    private readonly InputManager    _input;
    private readonly LightSystem     _lightSystem;
    private readonly ChunkMeshSystem _meshSystem;
    private readonly EntitySet       _cameras;
    private readonly GpuMesh         _cubeMesh;

    private readonly List<(BodyHandle body, Entity entity)> _spawned = new();

    public DebugDropSystem(World world, PhysicsWorld physics, InputManager input, LightSystem lightSystem, ChunkMeshSystem meshSystem, Renderer renderer)
    {
        _world       = world;
        _physics     = physics;
        _input       = input;
        _lightSystem = lightSystem;
        _meshSystem  = meshSystem;
        _cameras     = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();

        var (verts, idx) = PrimitiveFactory.Cube(new Vector3D<float>(0.9f, 0.3f, 0.2f));
        _cubeMesh = renderer.UploadMesh(verts, idx);
    }

    public void Update(float dt)
    {
        if (_input.WasKeyPressed(Key.B) && TryGetCamera(out var bt))
        {
            var spawn = bt.Position + Vec.Rotate(bt.Rotation, new Vector3D<float>(0, 0, -3));
            var body  = _physics.AddDynamicBox(new PhysVec(spawn.X, spawn.Y, spawn.Z), PhysVec.One, mass: 1f);

            var e = _world.CreateEntity();
            e.Set(Transform.Identity);
            e.Set(new MeshRenderer { Mesh = _cubeMesh });
            _spawned.Add((body, e));
            Console.WriteLine($"[debug] dropped box at ({spawn.X:0.0},{spawn.Y:0.0},{spawn.Z:0.0})");
        }

        // G: spawn a single-block dynamic voxel grid in front of the camera.
        if (_input.WasKeyPressed(Key.G) && TryGetCamera(out var gt))
        {
            var spawn = gt.Position + Vec.Rotate(gt.Rotation, new Vector3D<float>(0, 0, -3));
            DynamicGridFactory.SpawnSingleBlock(_world, _lightSystem, _meshSystem, new PhysVec(spawn.X, spawn.Y, spawn.Z), BlockId.Stone);
            Console.WriteLine($"[debug] spawned grid at ({spawn.X:0.0},{spawn.Y:0.0},{spawn.Z:0.0})");
        }

        foreach (var (body, entity) in _spawned)
        {
            if (!entity.IsAlive) continue;
            var (p, q) = _physics.GetBodyPose(body);
            ref var tr = ref entity.Get<Transform>();
            tr.Position = PhysicsConv.ToSilk(p);
            tr.Rotation = PhysicsConv.ToSilk(q);
        }
    }

    private bool TryGetCamera(out Transform transform)
    {
        foreach (ref readonly Entity e in _cameras.GetEntities())
        {
            ref readonly var cc = ref e.Get<CameraComponent>();
            if (cc.Active) { transform = e.Get<Transform>(); return true; }
        }
        transform = default;
        return false;
    }
}
