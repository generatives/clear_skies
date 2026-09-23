using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// A 3D model uploaded to the GPU by <see cref="Renderer.UploadModel"/>: one mesh + texture per material,
/// in model space, plus the model-space bounds <c>ModelRenderSystem</c> frustum-culls with. Shareable between any
/// number of <see cref="ModelRenderer"/> entities.
/// </summary>
public sealed class GpuModel : IDisposable
{
    public IReadOnlyList<GpuModelPart> Parts { get; }
    public Vector3D<float> BoundsMin { get; }
    public Vector3D<float> BoundsMax { get; }

    internal GpuModel(IReadOnlyList<GpuModelPart> parts, Vector3D<float> boundsMin, Vector3D<float> boundsMax)
    {
        Parts     = parts;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
    }

    public void Dispose()
    {
        foreach (var p in Parts)
        {
            p.Mesh.Dispose();
            p.Texture.Dispose();
        }
    }
}

/// <summary>One material's triangles: the mesh, its base-colour texture and the alpha-test cutoff (0 = opaque).</summary>
public sealed record GpuModelPart(GpuMesh Mesh, ModelTexture Texture, float AlphaCutoff);
