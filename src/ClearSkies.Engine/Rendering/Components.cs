using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;

/// <summary>
/// Marks an entity as drawable with a given GPU mesh and per-volume lighting info.
/// <see cref="Grid"/> null → drawn full-bright.
/// </summary>
public struct ChunkMesh
{
    public GpuMesh Mesh;

    /// <summary>Owning volume's registration in the shared voxel storage; the fragment shader looks light up
    /// through it. Null for non-chunk meshes (debug cubes, etc.), which draw full-bright.</summary>
    public GridHandle? Grid;

    /// <summary>This chunk's position in its volume (grid-local chunk coordinates).</summary>
    public ChunkPosition ChunkPos;
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