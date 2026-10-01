using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.Gltf;
using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// What other players are drawn as: there's no player model yet, so a body-sized box the size of the character capsule,
/// with a visor on the side they face (their Transform turns with their look's yaw). SpawnPlayerHandler gives each
/// player spawned here but owned elsewhere one; <see cref="ModelRenderSystem"/> draws it like any model. The local
/// player isn't drawn (the camera is in their head).
/// </summary>
public sealed class PlayerModel : IDisposable
{
    private readonly Renderer _renderer;
    private GpuModel? _model;

    public PlayerModel(Renderer renderer) => _renderer = renderer;

    /// <summary>A model for one player, with its own pose; the mesh is uploaded with the first.</summary>
    public RenderedModel Create() => new(_model ??= _renderer.UploadModel(BuildModel()));

    /// <summary>A 0.6 × 1.6 × 0.6 body centred on the capsule's centre, and a visor at eye height towards -Z.</summary>
    private static ModelData BuildModel()
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        void Box(Vector3D<float> centre, Vector3D<float> size, Vector3D<float> color)
        {
            var (v, i) = PrimitiveFactory.Cube(color);
            uint start = (uint)vertices.Count;
            foreach (var vertex in v)
            {
                var copy = vertex;
                copy.Position = centre + vertex.Position * size;
                vertices.Add(copy);
            }
            foreach (var index in i) indices.Add(start + index);
        }
        Box(Vector3D<float>.Zero, new(0.6f, 1.6f, 0.6f), new(0.25f, 0.45f, 0.8f));
        Box(new(0, 0.55f, -0.3f), new(0.45f, 0.15f, 0.1f), new(0.95f, 0.85f, 0.3f));
        var nodes = new[] { new ModelNode("body", -1, Vector3D<float>.Zero, Quaternion<float>.Identity, Vector3D<float>.One) };
        var parts = new[] { new ModelPart(0, vertices.ToArray(), indices.ToArray(), ModelMaterial.White) };
        return new ModelData(nodes, parts, new Vector3D<float>(-0.3f, -0.8f, -0.4f), new Vector3D<float>(0.3f, 0.8f, 0.3f));
    }

    public void Dispose() => _model?.Dispose();
}
