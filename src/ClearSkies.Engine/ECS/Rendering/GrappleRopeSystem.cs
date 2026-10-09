using ClearSkies.Engine.Core;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Every frame, presentation only: draws each <see cref="Grapple"/> rope as a black line from the player's hand to
/// the hook, both where they're drawn (so the rope stays on a moving ship's block and with the player between ticks).
/// Each player's rope is an entity with a two-point <see cref="WireframeRenderer"/> mesh, made at their first rope,
/// shown while they have one and disposed with the player. Only ropes this machine simulates are drawn: the host's
/// players', and a client's own.
/// </summary>
public sealed class GrappleRopeSystem : ISystem, IDisposable
{
    /// <summary>Where the rope leaves the player, in their space from the capsule's centre: their right hand.</summary>
    private static readonly Vector3D<float> Hand = new(0.3f, 0.2f, -0.2f);

    private static readonly Vector3D<float> Black = Vector3D<float>.Zero;

    private readonly Renderer _renderer;
    private readonly EntitySet _grappling;
    private readonly Dictionary<Entity, (Entity Line, GpuMesh Mesh)> _ropes = new();
    private readonly List<Entity> _gone = new();

    public GrappleRopeSystem(World world, Renderer renderer)
    {
        _renderer = renderer;
        _grappling = world.GetEntities().With<Transform>().With<Grapple>().AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity player in _grappling.GetEntities())
        {
            ref readonly var g = ref player.Get<Grapple>();
            if (!g.Anchor.IsAlive || !g.Anchor.Has<Transform>()) continue;
            if (!_ropes.TryGetValue(player, out var rope)) _ropes[player] = rope = Create(player.World);

            var p = player.DrawnPose();
            var start = p.Position + Vec.Rotate(p.Rotation, Hand);
            var end = g.WorldPoint(g.Anchor.DrawnPose());
            Span<Vertex> verts = stackalloc Vertex[2];
            verts[0] = new Vertex(Vector3D<float>.Zero, Vector3D<float>.UnitY, Black);
            verts[1] = new Vertex(end - start, Vector3D<float>.UnitY, Black);
            rope.Mesh.VertexBuffer.Write<Vertex>(0, verts);
            rope.Line.Get<Transform>().Position = start; // the line's points are relative to it, to keep them precise
        }

        // Ropes let go are hidden; their meshes are kept for the player's next rope until the player is gone.
        foreach (var (player, rope) in _ropes)
        {
            if (!player.IsAlive) { _gone.Add(player); continue; }
            bool shown = player.Has<Grapple>() && player.Get<Grapple>().Anchor is { IsAlive: true } a && a.Has<Transform>();
            if (shown != rope.Line.Has<WireframeRenderer>())
            {
                if (shown) rope.Line.Set(new WireframeRenderer { Mesh = rope.Mesh });
                else rope.Line.Remove<WireframeRenderer>();
            }
        }
        foreach (var player in _gone)
        {
            var (line, mesh) = _ropes[player];
            _ropes.Remove(player);
            if (line.IsAlive) line.Dispose();
            mesh.Dispose();
        }
        _gone.Clear();
    }

    private (Entity Line, GpuMesh Mesh) Create(World world)
    {
        var verts = new[] { new Vertex(default, Vector3D<float>.UnitY, Black), new Vertex(default, Vector3D<float>.UnitY, Black) };
        var mesh = _renderer.UploadMesh(verts, new uint[] { 0, 1, 1 }, new uint[] { 0, 1 }); // a dummy triangle, never drawn
        var line = world.CreateEntity();
        line.Set(Transform.Identity);
        return (line, mesh);
    }

    public void Dispose()
    {
        foreach (var (_, (line, mesh)) in _ropes)
        {
            if (line.IsAlive) line.Dispose();
            mesh.Dispose();
        }
        _ropes.Clear();
    }
}
