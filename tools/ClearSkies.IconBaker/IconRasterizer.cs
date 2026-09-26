using System.Numerics;
using ClearSkies.Engine.Rendering.Gltf;

namespace ClearSkies.IconBaker;

/// <summary>
/// Draws a <see cref="ModelData"/> into a small RGBA icon on the CPU. It uses a fixed orthographic 3/4 view, so every
/// icon has the same angle and lighting and the output is identical on every machine.
///
/// The model is posed at rest and turned by <see cref="YawDegrees"/> and then <see cref="PitchDegrees"/>. It is
/// scaled to fill the icon and centred. It is drawn at <see cref="Supersample"/> times the size with a depth buffer,
/// and averaged down so edges are anti-aliased. Materials are drawn as the engine draws them:
/// - base color times the texture, sampled nearest (Blockbench textures are pixel art);
/// - alpha-masked texels cut out, double-sided.
///
/// Lighting is one directional light fixed relative to the view (above, left and in front) plus ambient. Tops come
/// out brightest and the left face brighter than the right, the usual block-icon shading. An optional one-pixel dark
/// outline keeps the silhouette readable on any slot background.
/// </summary>
internal sealed class IconRasterizer
{
    public int Size { get; init; } = 32;
    public int Supersample { get; init; } = 4;
    public float YawDegrees { get; init; } = 225f;  // front (-Z, a model block's north face) and right side in view
    public float PitchDegrees { get; init; } = 30f; // looking down onto the top
    public bool Outline { get; init; } = true;

    private static readonly Vector3 LightInView = Vector3.Normalize(new Vector3(-0.45f, 1.0f, 0.6f));
    private const float Ambient = 0.45f, Diffuse = 0.6f;
    private static readonly Vector4 OutlineColor = new(24 / 255f, 18 / 255f, 14 / 255f, 1f);

    private readonly record struct ViewVertex(Vector3 Position, Vector3 Normal, Vector2 Uv, Vector3 Color);

    /// <summary>The icon as RGBA8, <see cref="Size"/> x <see cref="Size"/>, straight (not premultiplied) alpha.</summary>
    public byte[] Render(ModelData model)
    {
        var parts = TransformToView(model);
        int n = Size * Supersample;
        var color = new Vector4[n * n];
        var depth = new float[n * n];
        Array.Fill(depth, float.NegativeInfinity);

        // Fit the model's projected bounds into the icon (leaving room for the outline), centred.
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var (verts, _, _) in parts)
            foreach (var v in verts)
            {
                var p = new Vector2(v.Position.X, -v.Position.Y);
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }
        float margin = (Outline ? 1f : 0f) * Supersample;
        var extent = max - min;
        float scale = (n - 2 * margin) / MathF.Max(MathF.Max(extent.X, extent.Y), 1e-6f);
        var offset = new Vector2(n / 2f) - (min + max) / 2f * scale;

        foreach (var (verts, indices, material) in parts)
            for (int i = 0; i + 2 < indices.Length; i += 3)
                DrawTriangle(verts[indices[i]], verts[indices[i + 1]], verts[indices[i + 2]], material,
                             scale, offset, n, color, depth);

        var icon = Downsample(color, n);
        if (Outline) AddOutline(icon);
        return ToBytes(icon);
    }

    private List<(ViewVertex[] Verts, uint[] Indices, ModelMaterial Material)> TransformToView(ModelData model)
    {
        // Rest pose, parents first (System.Numerics row vectors: child transform first).
        var world = new Matrix4x4[model.Nodes.Count];
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            var node = model.Nodes[i];
            var local = Matrix4x4.CreateScale(node.Scale.X, node.Scale.Y, node.Scale.Z)
                      * Matrix4x4.CreateFromQuaternion(new Quaternion(node.Rotation.X, node.Rotation.Y, node.Rotation.Z, node.Rotation.W))
                      * Matrix4x4.CreateTranslation(node.Translation.X, node.Translation.Y, node.Translation.Z);
            world[i] = node.Parent < 0 ? local : local * world[node.Parent];
        }
        // The camera looks down -Z; turning the model instead: yaw about Y, then pitch about X.
        var view = Matrix4x4.CreateRotationY(YawDegrees * MathF.PI / 180f)
                 * Matrix4x4.CreateRotationX(PitchDegrees * MathF.PI / 180f);

        var result = new List<(ViewVertex[], uint[], ModelMaterial)>();
        foreach (var part in model.Parts)
        {
            var toView = (part.Node >= 0 ? world[part.Node] : Matrix4x4.Identity) * view;
            var verts = new ViewVertex[part.Vertices.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var v = part.Vertices[i];
                var normal = Vector3.TransformNormal(new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z), toView);
                verts[i] = new ViewVertex(
                    Vector3.Transform(new Vector3(v.Position.X, v.Position.Y, v.Position.Z), toView),
                    normal == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(normal),
                    new Vector2(v.Uv.X, v.Uv.Y),
                    new Vector3(v.Color.X, v.Color.Y, v.Color.Z));
            }
            result.Add((verts, part.Indices, part.Material));
        }
        return result;
    }

    private static void DrawTriangle(ViewVertex a, ViewVertex b, ViewVertex c, ModelMaterial material,
                                     float scale, Vector2 offset, int n, Vector4[] color, float[] depth)
    {
        // Screen space: x right, y down. Orthographic, so plain barycentric interpolation is exact.
        Vector2 S(in ViewVertex v) => new Vector2(v.Position.X, -v.Position.Y) * scale + offset;
        var pa = S(a); var pb = S(b); var pc = S(c);
        float area = Edge(pa, pb, pc);
        if (MathF.Abs(area) < 1e-8f) return;

        int x0 = System.Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X))));
        int y0 = System.Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y))));
        int x1 = System.Math.Min(n - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))));
        int y1 = System.Math.Min(n - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))));

        // Double-sided: a face seen from behind is lit as its front.
        var faceNormal = a.Normal + b.Normal + c.Normal;
        float facing = faceNormal.Z >= 0 ? 1f : -1f;

        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            float wa = Edge(pb, pc, p) / area, wb = Edge(pc, pa, p) / area, wc = Edge(pa, pb, p) / area;
            if (wa < 0 || wb < 0 || wc < 0) continue;

            float z = wa * a.Position.Z + wb * b.Position.Z + wc * c.Position.Z;
            int i = y * n + x;
            if (z <= depth[i]) continue; // larger z is nearer the camera

            var uv = wa * a.Uv + wb * b.Uv + wc * c.Uv;
            var texel = Sample(material.Texture, uv);
            if (material.AlphaCutoff > 0 && texel.W < material.AlphaCutoff) continue;

            var normal = Vector3.Normalize(wa * a.Normal + wb * b.Normal + wc * c.Normal) * facing;
            float light = Ambient + Diffuse * MathF.Max(0f, Vector3.Dot(normal, LightInView));
            var vertexColor = wa * a.Color + wb * b.Color + wc * c.Color;
            var rgb = new Vector3(texel.X, texel.Y, texel.Z) * vertexColor * light;
            depth[i] = z;
            color[i] = new Vector4(Vector3.Min(rgb, Vector3.One), 1f);
        }
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    private static Vector4 Sample(ModelTextureData? texture, Vector2 uv)
    {
        if (texture is null) return Vector4.One;
        int x = Wrap(uv.X, texture.Width, texture.ClampU), y = Wrap(uv.Y, texture.Height, texture.ClampV);
        int i = (y * texture.Width + x) * 4;
        var px = texture.Rgba;
        return new Vector4(px[i], px[i + 1], px[i + 2], px[i + 3]) / 255f;
    }

    private static int Wrap(float t, int size, bool clamp)
    {
        int i = (int)MathF.Floor(t * size);
        return clamp ? System.Math.Clamp(i, 0, size - 1) : ((i % size) + size) % size;
    }

    /// <summary>Averages each supersample block: alpha is coverage, color the mean of the covered samples.</summary>
    private Vector4[] Downsample(Vector4[] big, int n)
    {
        var icon = new Vector4[Size * Size];
        int ss = Supersample;
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            var sum = Vector4.Zero;
            for (int sy = 0; sy < ss; sy++)
            for (int sx = 0; sx < ss; sx++)
            {
                var c = big[(y * ss + sy) * n + x * ss + sx];
                sum += new Vector4(c.X * c.W, c.Y * c.W, c.Z * c.W, c.W);
            }
            icon[y * Size + x] = sum.W > 0
                ? new Vector4(sum.X / sum.W, sum.Y / sum.W, sum.Z / sum.W, sum.W / (ss * ss))
                : Vector4.Zero;
        }
        return icon;
    }

    /// <summary>Puts the outline color, behind the icon, on pixels next to (4-neighbour) mostly-covered ones.</summary>
    private void AddOutline(Vector4[] icon)
    {
        var solid = new bool[icon.Length];
        for (int i = 0; i < icon.Length; i++) solid[i] = icon[i].W >= 0.5f;
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            int i = y * Size + x;
            if (solid[i]) continue;
            bool edge = (x > 0 && solid[i - 1]) || (x < Size - 1 && solid[i + 1])
                     || (y > 0 && solid[i - Size]) || (y < Size - 1 && solid[i + Size]);
            if (!edge) continue;
            // "Over" the outline: the partial coverage already here stays on top.
            var c = icon[i];
            var rgb = new Vector3(c.X, c.Y, c.Z) * c.W + new Vector3(OutlineColor.X, OutlineColor.Y, OutlineColor.Z) * (1 - c.W);
            icon[i] = new Vector4(rgb, 1f);
        }
    }

    private static byte[] ToBytes(Vector4[] icon)
    {
        var bytes = new byte[icon.Length * 4];
        for (int i = 0; i < icon.Length; i++)
        {
            var c = Vector4.Clamp(icon[i], Vector4.Zero, Vector4.One) * 255f + new Vector4(0.5f);
            bytes[i * 4] = (byte)c.X; bytes[i * 4 + 1] = (byte)c.Y; bytes[i * 4 + 2] = (byte)c.Z; bytes[i * 4 + 3] = (byte)c.W;
        }
        return bytes;
    }
}
