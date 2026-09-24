using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;

/// <summary>
/// Everything drawn for one loaded chunk (see ChunkRenderSystem): its greedy-meshed cubes and its model blocks.
/// Set by ChunkMeshSystem whenever the chunk remeshes; absent while the chunk has nothing to draw.
/// </summary>
public struct ChunkRenderData
{
    /// <summary>The chunk's cube faces, or null when it holds only model blocks.</summary>
    public GpuMesh? Mesh;

    /// <summary>Every static model block (<see cref="BlockDef.Model"/>, not an entity block) in the chunk; empty
    /// when there are none.</summary>
    public ModelBlock[] Models;

    /// <summary>Owning volume's registration in the shared voxel storage; the fragment shader looks light up
    /// through it.</summary>
    public GridHandle? Grid;

    /// <summary>This chunk's position in its volume (grid-local chunk coordinates).</summary>
    public ChunkPosition ChunkPos;
}

/// <summary>One placed model block: its shared model, cell in the chunk (chunk-local voxel coordinates) and the
/// voxel's stored facing, which the model's +Y is turned to point along.</summary>
public readonly record struct ModelBlock(GpuModel Model, BlockId Block, byte X, byte Y, byte Z, Facing Facing);

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
/// <summary>Lights a <see cref="ModelRenderer"/> entity from one voxel's stored light instead of just sun and
/// ambient: its volume's registration, and the cell's chunk and chunk-local position. Block entities get it (see
/// <c>BlockModelSystem</c>), so they are lit like the static model blocks around them.</summary>
public struct VoxelLit
{
    public GridHandle Grid;
    public ChunkPosition Chunk;
    public Silk.NET.Maths.Vector3D<int> Cell;
}

/// <summary>Draws a 3D model (e.g. a glTF prop loaded via <see cref="ClearSkies.Engine.Rendering.Gltf.GltfLoader"/>
/// and uploaded with <c>Renderer.UploadModel</c>) at the entity's <c>Transform</c>, by <c>ModelRenderSystem</c>. The
/// model can be shared by many entities; one that animates also carries its own <c>AnimatedModel</c>.</summary>
public struct ModelRenderer
{
    public GpuModel Model;
}
