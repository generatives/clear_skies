using System.Runtime.InteropServices;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering.WebGpu;
using DefaultEcs;
using Silk.NET.Maths;
using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Moves and draws the particles of every drawn entity's <see cref="ParticleEmitter"/>: each frame
/// (<see cref="SystemStage.Frame"/>) it updates each emitter at its entity's drawn pose, and in
/// <see cref="SystemStage.RenderWorld"/> draws them all as one instanced draw of camera-facing squares, opaque and
/// depth-writing, so they need no sorting. Up to <see cref="MaxParticles"/> are drawn in all, from the nearest
/// emitters out. Purely cosmetic: each
/// machine runs its own.
/// </summary>
public sealed class ParticleRenderer : ISystem, IRenderSystem, IDisposable
{
    public const int MaxParticles = 8192;

    /// <summary>One particle as vs_particle reads it: its centre and size (blocks), and its colour (sRGB).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public Vector4D<float> PositionSize;
        public Vector4D<float> Color;
    }

    private readonly Renderer _renderer;
    private readonly EntitySet _emitters;
    private readonly GpuBuffer _buffer;
    private readonly Instance[] _instances = new Instance[MaxParticles];
    private readonly List<(float Distance, ParticleEmitter Emitter)> _byDistance = new();

    public ParticleRenderer(World world, Renderer renderer)
    {
        _renderer = renderer;
        _emitters = world.GetEntities().With<ParticleEmitter>().With<Transform>().With<Rendered>().AsSet();
        _buffer = GpuBuffer.Create(renderer.Context, (ulong)(MaxParticles * Marshal.SizeOf<Instance>()),
                                   BufferUsage.Vertex | BufferUsage.CopyDst);
    }

    public void Update(float dt)
    {
        foreach (ref readonly var e in _emitters.GetEntities())
            e.Get<ParticleEmitter>().Update(dt, e.DrawnPose());
    }

    public void Render(SystemStage stage, in RenderContext frame)
    {
        // Nearest emitters first, so if there are more than MaxParticles the far ones are the ones left out.
        _byDistance.Clear();
        foreach (ref readonly var e in _emitters.GetEntities())
        {
            var emitter = e.Get<ParticleEmitter>();
            if (emitter.Count > 0)
                _byDistance.Add((Vector3D.DistanceSquared(e.DrawnPose().Position, frame.CameraPosition), emitter));
        }
        _byDistance.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        int count = 0;
        foreach (var (_, emitter) in _byDistance)
            count += emitter.Write(_instances.AsSpan(count));
        if (count == 0) return;
        _buffer.Write<Instance>(0, _instances.AsSpan(0, count));
        _renderer.DrawParticles(_buffer, (uint)count);
    }

    public void Dispose() => _buffer.Dispose();
}
