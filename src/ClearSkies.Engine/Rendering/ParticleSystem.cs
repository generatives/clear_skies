using System.Runtime.InteropServices;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.Maths;
using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Short-lived camera-facing squares (thruster flames, for now): a fixed pool moved on the CPU each frame
/// (<see cref="SystemStage.Frame"/>) and drawn as one instanced draw in <see cref="SystemStage.RenderWorld"/>. Each
/// flies at the velocity it was spawned with and shrinks to nothing over its life. Opaque and depth-writing, so they
/// need no sorting. Purely cosmetic: each machine spawns its own.
/// </summary>
public sealed class ParticleSystem : ISystem, IRenderSystem, IDisposable
{
    public const int MaxParticles = 4096;

    /// <summary>One particle as vs_particle reads it: its centre and size (blocks), and its colour (sRGB).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public Vector4D<float> PositionSize;
        public Vector4D<float> Color;
    }

    private struct Particle
    {
        public Vector3D<float> Position, Velocity, Color;
        public float Size, Age, Life;
    }

    private readonly Renderer _renderer;
    private readonly GpuBuffer _buffer;
    private readonly Particle[] _particles = new Particle[MaxParticles];
    private readonly Instance[] _instances = new Instance[MaxParticles];
    private int _count;

    public ParticleSystem(Renderer renderer)
    {
        _renderer = renderer;
        _buffer = GpuBuffer.Create(renderer.Context, (ulong)(MaxParticles * Marshal.SizeOf<Instance>()),
                                   BufferUsage.Vertex | BufferUsage.CopyDst);
    }

    /// <summary>Particles alive now.</summary>
    public int Count => _count;

    /// <summary>Adds a particle of <paramref name="size"/> blocks that lives <paramref name="life"/> seconds; dropped
    /// if the pool is full.</summary>
    public void Spawn(Vector3D<float> position, Vector3D<float> velocity, float size, float life, Vector3D<float> color)
    {
        if (_count == MaxParticles || life <= 0f) return;
        _particles[_count++] = new Particle { Position = position, Velocity = velocity, Color = color, Size = size, Life = life };
    }

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
    }

    public void Render(SystemStage stage, in RenderContext frame)
    {
        if (_count == 0) return;
        for (int i = 0; i < _count; i++)
        {
            ref readonly var p = ref _particles[i];
            float size = p.Size * (1f - p.Age / p.Life);
            _instances[i] = new Instance
            {
                PositionSize = new Vector4D<float>(p.Position, size),
                Color        = new Vector4D<float>(p.Color, 1f),
            };
        }
        _buffer.Write<Instance>(0, _instances.AsSpan(0, _count));
        _renderer.DrawParticles(_buffer, (uint)_count);
    }

    public void Dispose() => _buffer.Dispose();
}
