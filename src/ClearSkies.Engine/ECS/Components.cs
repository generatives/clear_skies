using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Marks an entity as drawable with a given GPU mesh and per-volume lighting info.
/// <see cref="VolumeGpu"/> null → renderer falls back to the shared full-bright buffer.
/// </summary>
public struct MeshRenderer
{
    public GpuMesh Mesh;

    /// <summary>Owning volume's GPU resources. The renderer reads <c>VolumeGpu.RenderBindGroup</c> for the
    /// light buffer, and derives this chunk's voxel base + the volume dims from it live each draw. Null for
    /// non-chunk meshes (debug cubes, etc.) — uses the shared full-bright fallback.</summary>
    internal VolumeGpuResources? VolumeGpu;

    /// <summary>This chunk's position in the volume. The fragment-shader chunkBase and volSize are computed
    /// from this against <see cref="VolumeGpu"/> at draw time, so a volume reallocation (which moves every
    /// chunk's base and changes the dims) needs no remesh — the geometry is unchanged.</summary>
    public ChunkPosition ChunkPos;
}

/// <summary>Tags the root entity of a dynamic voxel grid, carrying a reference to its data/body.</summary>
public struct DynamicGridComponent
{
    public DynamicGrid Grid;
}

/// <summary>Marks an entity as a camera. Only the first active camera is used for rendering.</summary>
public struct CameraComponent
{
    public Camera Camera;
    public bool Active;
}

/// <summary>Always renders the mesh as a wireframe overlay regardless of the global WireframeMode.</summary>
public struct WireframeRenderer
{
    public GpuMesh Mesh;
}

/// <summary>Renders the mesh in screen space (HUD pipeline: depth always passes, no depth write). Vertices are in NDC.</summary>
public struct HudRenderer
{
    public GpuMesh Mesh;
}

/// <summary>Free-fly camera control parameters and accumulated look angles.</summary>
public struct FreeFlyController
{
    public float MoveSpeed;
    public float LookSensitivity;
    public float Yaw;
    public float Pitch;
}
