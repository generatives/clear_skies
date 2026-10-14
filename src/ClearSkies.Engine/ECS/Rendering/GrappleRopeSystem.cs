using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Draws every player's grapple rope as a black cord <see cref="Width"/> thick, turned to face the camera, from their
/// right hand to the hook, both where they're drawn (so the rope stays on a moving ship's block and with the player
/// between ticks). Runs in <see cref="SystemStage.RenderOverlay"/>.
/// <list type="bullet">
/// <item>Players simulated here (the host's, a client's own) draw their <see cref="Grapple"/>; everyone else's draw
/// their <see cref="RopeHook"/>, replicated from the host.</item>
/// <item>The player looked through starts the rope at <see cref="FirstPersonHand"/>, in front of the eye, low on the
/// right of the view, so it reads as held rather than running off the bottom of the screen; others start it at
/// <see cref="Hand"/>, from their capsule's centre.</item>
/// </list>
/// Each player's rope is a four-point mesh, made at their first rope, rewritten each frame they have one, and disposed
/// with the player.
/// </summary>
public sealed class GrappleRopeSystem : IRenderSystem, IDisposable
{
    /// <summary>How thick the rope is drawn, in blocks.</summary>
    public const float Width = 0.02f;

    /// <summary>Where another player's rope leaves them, in their space from the capsule's centre: their right hand.</summary>
    private static readonly Vector3D<float> Hand = new(0.3f, 0.2f, -0.2f);

    /// <summary>Where the rope leaves the player looked through, in the camera's space: low and right, in view.</summary>
    private static readonly Vector3D<float> FirstPersonHand = new(0.3f, -0.25f, -0.6f);

    private static readonly Vector3D<float> Black = Vector3D<float>.Zero;

    private readonly Renderer _renderer;
    private readonly EntityRegistry _registry;
    private readonly EntitySet _players;
    private readonly EntitySet _cameras;
    private readonly Dictionary<Entity, GpuMesh> _ropes = new();
    private readonly List<Entity> _gone = new();

    public GrappleRopeSystem(World world, Renderer renderer, EntityRegistry registry)
    {
        _renderer = renderer;
        _registry = registry;
        _players = world.GetEntities().With<Transform>().With<Player>().AsSet();
        _cameras = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
    }

    public void Render(SystemStage stage, in RenderContext frame)
    {
        bool hasCamera = CameraUtil.TryGetActive(_cameras, out var camera);
        bool firstPerson = hasCamera && ActiveCameraIsEye();
        foreach (ref readonly Entity player in _players.GetEntities())
        {
            if (!hasCamera || !TryGetHook(player, out var end)) continue;
            var hand = firstPerson && player.Has<LocalPlayer>() ? camera : player.DrawnPose();
            var start = hand.Position + Vec.Rotate(hand.Rotation, firstPerson && player.Has<LocalPlayer>() ? FirstPersonHand : Hand);
            if (!_ropes.TryGetValue(player, out var mesh)) _ropes[player] = mesh = Create();
            if (!Write(mesh, start, end, camera.Position)) continue;
            var at = Transform.Identity;
            at.Position = start; // the rope's points are relative to it, to keep them precise
            _renderer.DrawMesh(mesh, at.ToMatrix());
        }

        foreach (var (player, _) in _ropes)
            if (!player.IsAlive) _gone.Add(player);
        foreach (var player in _gone)
        {
            _ropes[player].Dispose();
            _ropes.Remove(player);
        }
        _gone.Clear();
    }

    /// <summary>Where <paramref name="player"/>'s rope is hooked, as drawn, if they have one.</summary>
    private bool TryGetHook(Entity player, out Vector3D<float> hook)
    {
        hook = default;
        if (player.Has<CharacterControllerComponent>()) // simulated here
        {
            if (!player.Has<Grapple>()) return false;
            ref readonly var g = ref player.Get<Grapple>();
            if (!g.Anchor.IsAlive || !g.Anchor.Has<Transform>()) return false;
            hook = g.WorldPoint(g.Anchor.DrawnPose());
            return true;
        }
        if (!player.Has<RopeHook>()) return false;
        ref readonly var h = ref player.Get<RopeHook>();
        if (h.Anchor.IsNone || !_registry.TryGet(h.Anchor, out var anchor) || !anchor.Has<Transform>()) return false;
        var pose = anchor.DrawnPose();
        hook = pose.Position + Vec.Rotate(pose.Rotation, h.LocalPoint);
        return true;
    }

    private bool ActiveCameraIsEye()
    {
        foreach (ref readonly Entity e in _cameras.GetEntities())
            if (e.Get<CameraComponent>().Active) return e.Has<Eye>();
        return false;
    }

    /// <summary>Writes the rope from <paramref name="start"/> to <paramref name="end"/> as a strip facing
    /// <paramref name="eye"/>, its points relative to <paramref name="start"/>; false if there's no strip to draw.</summary>
    private static bool Write(GpuMesh mesh, Vector3D<float> start, Vector3D<float> end, Vector3D<float> eye)
    {
        var along = end - start;
        var side = Vector3D.Cross(along, eye - start);
        float length = side.Length;
        if (length < 1e-6f) return false; // looking straight down it: nothing to see
        side *= Width / 2 / length;
        var normal = Vector3D.Normalize(eye - start);
        Span<Vertex> verts = stackalloc Vertex[4];
        verts[0] = new Vertex(-side, normal, Black);
        verts[1] = new Vertex(side, normal, Black);
        verts[2] = new Vertex(along + side, normal, Black);
        verts[3] = new Vertex(along - side, normal, Black);
        mesh.VertexBuffer.Write<Vertex>(0, verts);
        return true;
    }

    private GpuMesh Create()
    {
        var verts = new Vertex[4];
        uint[] tris = { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 }; // both windings, so it shows whichever way it turns
        uint[] edges = { 0, 1, 1, 2, 2, 3, 3, 0 };
        return _renderer.UploadMesh(verts, tris, edges);
    }

    public void Dispose()
    {
        foreach (var (_, mesh) in _ropes) mesh.Dispose();
        _ropes.Clear();
    }
}
