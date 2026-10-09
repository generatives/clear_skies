using ClearSkies.Engine.Core;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Blows flames (red squares, for now) out of the exhaust (top) face of every drawn <see cref="Fan"/> that is
/// thrusting, more and faster the harder it pushes (<see cref="Fan.Thrust"/>). Runs each frame, before the
/// <see cref="ParticleSystem"/> moves them.
/// </summary>
public sealed class ThrusterFlameSystem : ISystem
{
    private const float MinThrust  = 0.02f; // below this a Fan counts as off
    private const float MaxRate    = 60f;   // flames per second at full thrust
    private const float Size       = 0.3f;  // blocks, when spawned
    private const float MinSpeed   = 3f, MaxSpeed = 8f; // blocks per second out of the exhaust, idle to full thrust
    private static readonly Vector3D<float> Red = new(1f, 0.1f, 0.05f);

    private readonly EntitySet _fans;
    private readonly ParticleSystem _particles;
    private readonly Random _random = new();

    public ThrusterFlameSystem(World world, ParticleSystem particles)
    {
        _particles = particles;
        _fans = world.GetEntities().With<Fan>().With<BlockRef>().With<Rendered>().AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _fans.GetEntities())
        {
            float thrust = e.Get<Fan>().Thrust;
            if (thrust < MinThrust) continue;

            // Whole flames this frame, and the fraction left over by chance, so low thrust still gives a trickle.
            float expected = MaxRate * thrust * dt;
            int n = (int)expected + (_random.NextSingle() < expected - (int)expected ? 1 : 0);
            if (n == 0) continue;

            ref readonly var block = ref e.Get<BlockRef>();
            var root = block.Volume.Root.DrawnPose();
            var up = block.Orientation.Up.ToVector();
            var exhaust = Vec.Rotate(root.Rotation, new Vector3D<float>(up.X, up.Y, up.Z));
            var p = block.Position;
            var nozzle = block.Volume.VoxelToWorld(root, new Vector3D<float>(p.X + 0.5f, p.Y + 0.5f, p.Z + 0.5f)) + exhaust * 0.55f;
            float speed = MinSpeed + (MaxSpeed - MinSpeed) * thrust;

            for (int i = 0; i < n; i++)
            {
                var jitter = new Vector3D<float>(Spread(0.3f), Spread(0.3f), Spread(0.3f));
                var drift  = new Vector3D<float>(Spread(0.6f), Spread(0.6f), Spread(0.6f));
                float life = 0.25f + 0.2f * _random.NextSingle();
                _particles.Spawn(nozzle + jitter, exhaust * speed + drift, Size, life, Red);
            }
        }
    }

    private float Spread(float half) => (2f * _random.NextSingle() - 1f) * half;
}
