using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Math;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>How a <see cref="ParticleEmitter"/>'s particles spawn and move. Speeds and the spawn rate scale with the
/// emitter's <see cref="ParticleEmitter.Intensity"/>.</summary>
public struct ParticleSettings
{
    /// <summary>Particles per second at full intensity.</summary>
    public float Rate;

    /// <summary>How long each lives, in seconds: a random time between these.</summary>
    public float MinLife, MaxLife;

    /// <summary>Where particles spawn, in the emitting entity's space.</summary>
    public Vector3D<float> Origin;

    /// <summary>Which way they fly, in the emitting entity's space (unit).</summary>
    public Vector3D<float> Direction;

    /// <summary>Speed along <see cref="Direction"/>, in blocks per second: from the first
    /// at no intensity to the second at full.</summary>
    public float MinSpeed, MaxSpeed;

    /// <summary>How far from the origin each spawns, at most, along each axis (blocks).</summary>
    public float Spread;

    /// <summary>Random velocity added along each axis, at most (blocks per second).</summary>
    public float Drift;

    /// <summary>Size when spawned (blocks); each shrinks to nothing over its life.</summary>
    public float Size;

    /// <summary>Colour, sRGB (drawn unlit).</summary>
    public Vector3D<float> Color;

    /// <summary>The most alive at once: past it, each new one replaces the oldest.</summary>
    public int Capacity;
}

/// <summary>
/// A component that emits particles from its entity: the settings they spawn and move by (including where on the
/// entity they come from and which way they fly, in the entity's own space, so the emitter follows the entity's pose
/// through the transform hierarchy), how hard (<see cref="Intensity"/>, which its owner sets), and the particles
/// alive. The <see cref="ParticleRenderer"/> moves
/// and draws every drawn entity's.
/// </summary>
public sealed class ParticleEmitter
{
    private struct Particle
    {
        public Vector3D<float> Position, Velocity;
        public float Age, Life;
    }

    private readonly Particle[] _particles;
    private int _count;
    private float _toSpawn; // fraction of a particle carried to the next frame

    public ParticleEmitter(in ParticleSettings settings)
    {
        Settings = settings;
        _particles = new Particle[System.Math.Max(1, settings.Capacity)];
    }

    public ParticleSettings Settings { get; }

    /// <summary>0 (none spawn) to 1 (full rate and speed).</summary>
    public float Intensity;

    public int Count => _count;

    /// <summary>Spawns this frame's new particles from the entity at <paramref name="pose"/> (where it's drawn), and
    /// moves and ages the rest. Particles already out fly on in world space: they don't follow the entity.</summary>
    public void Update(float dt, in Transform pose)
    {
        for (int i = 0; i < _count;)
        {
            ref var p = ref _particles[i];
            p.Age += dt;
            if (p.Age >= p.Life) { p = _particles[--_count]; continue; } // swap the last one in
            p.Position += p.Velocity * dt;
            i++;
        }

        if (Intensity <= 0f) { _toSpawn = 0f; return; }
        _toSpawn += Settings.Rate * Intensity * dt;
        float speed = Settings.MinSpeed + (Settings.MaxSpeed - Settings.MinSpeed) * Intensity;
        var origin = pose.Position + Vec.Rotate(pose.Rotation, Settings.Origin);
        var direction = Vec.Rotate(pose.Rotation, Settings.Direction);
        for (; _toSpawn >= 1f; _toSpawn -= 1f)
        {
            int slot = _count < _particles.Length ? _count++ : Oldest();
            _particles[slot] = new Particle
            {
                Position = origin + Random3(Settings.Spread),
                Velocity = direction * speed + Random3(Settings.Drift),
                Life     = Settings.MinLife + (Settings.MaxLife - Settings.MinLife) * Random.Shared.NextSingle(),
            };
        }
    }

    /// <summary>Writes the live particles into <paramref name="into"/> (as many as fit); returns how many.</summary>
    public int Write(Span<ParticleRenderer.Instance> into)
    {
        int n = System.Math.Min(_count, into.Length);
        var color = new Vector4D<float>(Settings.Color, 1f);
        for (int i = 0; i < n; i++)
        {
            ref readonly var p = ref _particles[i];
            into[i] = new ParticleRenderer.Instance
            {
                PositionSize = new Vector4D<float>(p.Position, Settings.Size * (1f - p.Age / p.Life)),
                Color        = color,
            };
        }
        return n;
    }

    private int Oldest()
    {
        int oldest = 0;
        for (int i = 1; i < _count; i++)
            if (_particles[i].Age > _particles[oldest].Age) oldest = i;
        return oldest;
    }

    private static Vector3D<float> Random3(float half) =>
        new(Jitter(half), Jitter(half), Jitter(half));

    private static float Jitter(float half) => (2f * Random.Shared.NextSingle() - 1f) * half;
}
