using ClearSkies.Engine.Core;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Gives every drawn <see cref="Fan"/> a flame <see cref="ParticleEmitter"/> (red squares, for now) and points it out
/// of the Fan's exhaust (top) face each frame, as strong as the Fan's thrust (<see cref="Fan.Thrust"/>): more flames,
/// and faster, the harder it pushes. Runs before the <see cref="ParticleRenderer"/> moves them.
/// </summary>
public sealed class ThrusterFlameSystem : ISystem
{
    private const float MinThrust = 0.02f; // below this a Fan counts as off

    public static readonly ParticleSettings Flame = new()
    {
        Rate = 60f, MinLife = 0.25f, MaxLife = 0.45f, MinSpeed = 3f, MaxSpeed = 8f,
        Spread = 0.3f, Drift = 0.6f, Size = 0.3f, Color = new Vector3D<float>(1f, 0.1f, 0.05f), Capacity = 64,
    };

    private readonly EntitySet _fans;

    public ThrusterFlameSystem(World world)
    {
        _fans = world.GetEntities().With<Fan>().With<BlockRef>().With<Rendered>().AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _fans.GetEntities())
        {
            if (!e.Has<ParticleEmitter>()) e.Set(new ParticleEmitter { Group = new ParticleGroup(Flame) });
            var flames = e.Get<ParticleEmitter>().Group;

            float thrust = e.Get<Fan>().Thrust;
            flames.Intensity = thrust < MinThrust ? 0f : thrust;
            if (flames.Intensity == 0f) continue;

            ref readonly var block = ref e.Get<BlockRef>();
            var root = block.Volume.Root.DrawnPose();
            var up = block.Orientation.Up.ToVector();
            flames.Direction = Vec.Rotate(root.Rotation, new Vector3D<float>(up.X, up.Y, up.Z));
            var p = block.Position;
            flames.Origin = block.Volume.VoxelToWorld(root, new Vector3D<float>(p.X + 0.5f, p.Y + 0.5f, p.Z + 0.5f))
                          + flames.Direction * 0.55f;
        }
    }
}
