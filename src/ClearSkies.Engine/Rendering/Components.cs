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
/// <summary>Draws a 3D model (e.g. a glTF prop loaded via <see cref="ClearSkies.Engine.Rendering.Gltf.GltfLoader"/>
/// and uploaded with <c>Renderer.UploadModel</c>) at the entity's <c>Transform</c>. The model can be shared by
/// many entities.</summary>
public struct ModelRenderer
{
    public GpuModel Model;
}
