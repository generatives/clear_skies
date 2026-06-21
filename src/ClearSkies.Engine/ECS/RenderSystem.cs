using ClearSkies.Engine.Core;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>Builds the camera uniform and issues a draw call per <see cref="MeshRenderer"/> entity.</summary>
public sealed class RenderSystem : ISystem
{
    private readonly EntitySet _cameras;
    private readonly EntitySet _meshes;
    private readonly EntitySet _wireframes;
    private readonly EntitySet _huds;
    private readonly Renderer _renderer;

    public RenderSystem(World world, Renderer renderer)
    {
        _renderer   = renderer;
        _cameras    = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _meshes     = world.GetEntities().With<Transform>().With<MeshRenderer>().AsSet();
        _wireframes = world.GetEntities().With<Transform>().With<WireframeRenderer>().AsSet();
        _huds       = world.GetEntities().With<HudRenderer>().AsSet();
    }

    public void Update(float dt)
    {
        if (!TryGetActiveCamera(out var camTransform, out var camera))
            return;

        var uniform = new CameraUniform
        {
            View         = camera.GetView(camTransform),
            Projection   = camera.GetProjection(_renderer.AspectRatio),
            SunDirection = Vector3D.Normalize(new Vector3D<float>(-0.4f, -1f, -0.3f)),
        };

        if (!_renderer.BeginFrame())
            return;

        _renderer.SetCameraUniform(uniform);

        foreach (ref readonly Entity e in _meshes.GetEntities())
        {
            ref readonly var t   = ref e.Get<Transform>();
            ref readonly var mr  = ref e.Get<MeshRenderer>();

            // Derive chunkBase + volume dims live from the current volume state. This stays correct
            // across volume reallocations (which move every chunk's base and resize the volume)
            // without needing to remesh. Fallback for non-chunk meshes: base 0, size 32 (full-bright).
            nint lbg = 0;
            int cbx = 0, cby = 0, cbz = 0;
            int vsx = ChunkData.Size, vsy = ChunkData.Size, vsz = ChunkData.Size;
            if (mr.VolumeGpu is { } gpu)
            {
                lbg = gpu.RenderBindGroup;
                var (bx, by, bz) = gpu.ChunkVoxelBase(mr.ChunkPos);
                cbx = bx; cby = by; cbz = bz;
                vsx = gpu.VW; vsy = gpu.VH; vsz = gpu.VD;
            }

            _renderer.DrawMesh(mr.Mesh, t.ToMatrix(), lbg, cbx, cby, cbz, vsx, vsy, vsz);
        }

        // Wireframe overlays drawn on top (pipeline switches mid-pass then restores).
        foreach (ref readonly Entity e in _wireframes.GetEntities())
        {
            ref readonly var t  = ref e.Get<Transform>();
            ref readonly var wr = ref e.Get<WireframeRenderer>();
            _renderer.DrawMeshWireframe(wr.Mesh, t.ToMatrix());
        }

        // HUD elements: screen-space NDC vertices, depth always passes.
        _renderer.BeginHudPass();
        foreach (ref readonly Entity e in _huds.GetEntities())
        {
            ref readonly var hr = ref e.Get<HudRenderer>();
            _renderer.DrawHudMesh(hr.Mesh, Mat4.Identity);
        }

        _renderer.EndFrame();
    }

    private bool TryGetActiveCamera(out Transform transform, out Camera camera)
    {
        foreach (ref readonly Entity e in _cameras.GetEntities())
        {
            ref readonly var cc = ref e.Get<CameraComponent>();
            if (cc.Active)
            {
                transform = e.Get<Transform>();
                camera = cc.Camera;
                return true;
            }
        }
        transform = default;
        camera = null!;
        return false;
    }
}
