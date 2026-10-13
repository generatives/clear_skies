using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Gives every drawn <see cref="Fan"/> a flame <see cref="ParticleEmitter"/> (red squares, for now) blowing out of its
/// exhaust (top) face, and each frame sets it as strong as the Fan's thrust (<see cref="Fan.Thrust"/>): more flames,
/// and faster, the harder it pushes. The emitter follows the Fan's block entity through the transform hierarchy, whose
/// origin is the middle of the face opposite its top, with its top along local up. Runs before the
/// <see cref="ParticleRenderer"/> moves them.
/// </summary>
public sealed class ThrusterFlameSystem : ISystem
{
    private const float MinThrust = 0.005f;  // below this a Fan counts as off
    private const float MinIntensity = 0.3f; // a Fan that's on burns at least this hard, so lightly loaded ones still show

    public static readonly ParticleSettings Flame = new()
    {
        Rate = 60f, MinLife = 0.25f, MaxLife = 0.45f, MinSpeed = 3f, MaxSpeed = 8f,
        Spread = 0.3f, Drift = 0.6f, Size = 0.15f, Color = new Vector3D<float>(1f, 0.1f, 0.05f), Capacity = 64,
    };

    private readonly EntitySet _fans;

    public ThrusterFlameSystem(World world)
    {
        _fans = world.GetEntities().With<Fan>().With<Rendered>().AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _fans.GetEntities())
        {
            if (!e.Has<ParticleEmitter>())
                e.Set(new ParticleEmitter(Flame) { LocalOffset = new Vector3D<float>(0f, 1.05f, 0f) }); // just past the top face
            float thrust = e.Get<Fan>().Thrust;
            e.Get<ParticleEmitter>().Intensity = thrust < MinThrust ? 0f : MinIntensity + (1f - MinIntensity) * thrust;
        }
    }
}
