using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>How a <see cref="ParticleGroup"/>'s particles spawn and move. Speeds and the spawn rate scale with the
/// group's <see cref="ParticleGroup.Intensity"/>.</summary>
public struct ParticleSettings
{
    /// <summary>Particles per second at full intensity.</summary>
    public float Rate;

    /// <summary>How long each lives, in seconds: a random time between these.</summary>
    public float MinLife, MaxLife;

    /// <summary>Speed along the group's <see cref="ParticleGroup.Direction"/>, in blocks per second: from the first
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
/// One emitter's particles: the settings they spawn and move by, where they come from and how hard
/// (<see cref="Origin"/>, <see cref="Direction"/>, <see cref="Intensity"/>, which its owner sets each frame), and the
/// ones alive. Put one on an entity with a <see cref="ParticleEmitter"/>; the <see cref="ParticleRenderer"/> moves and
/// draws every drawn entity's.
/// </summary>
public sealed class ParticleGroup
{
    private struct Particle
    {
        public Vector3D<float> Position, Velocity;
        public float Age, Life;
    }

    private readonly Particle[] _particles;
    private int _count;
    private float _toSpawn; // fraction of a particle carried to the next frame

    public ParticleGroup(in ParticleSettings settings)
    {
        Settings = settings;
        _particles = new Particle[System.Math.Max(1, settings.Capacity)];
    }

    public ParticleSettings Settings { get; }

    /// <summary>Where particles spawn, in world space.</summary>
    public Vector3D<float> Origin;

    /// <summary>Which way they fly, in world space (unit).</summary>
    public Vector3D<float> Direction;

    /// <summary>0 (none spawn) to 1 (full rate and speed).</summary>
    public float Intensity;

    public int Count => _count;

    /// <summary>Spawns this frame's new particles and moves and ages the rest.</summary>
    public void Update(float dt)
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
        for (; _toSpawn >= 1f; _toSpawn -= 1f)
        {
            int slot = _count < _particles.Length ? _count++ : Oldest();
            _particles[slot] = new Particle
            {
                Position = Origin + Random3(Settings.Spread),
                Velocity = Direction * speed + Random3(Settings.Drift),
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

/// <summary>Gives an entity a <see cref="ParticleGroup"/> for the <see cref="ParticleRenderer"/> to move and draw
/// while the entity is drawn.</summary>
public struct ParticleEmitter
{
    public ParticleGroup Group;
}
