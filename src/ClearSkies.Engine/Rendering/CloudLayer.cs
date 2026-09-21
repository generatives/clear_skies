using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Minecraft-style blocky clouds: a flat layer of <see cref="CellSize"/>-block square cells, each either a
/// <see cref="Thickness"/>-block-thick slab of cloud or empty, drawn as real depth-tested geometry so islands and
/// ships sort against them properly. The cell pattern is one repeating tile (<see cref="Cells"/>² cells, built from
/// wrapping value noise so it tiles seamlessly); 3×3 copies around the camera always reach past the cloud fog's
/// end, and the whole layer drifts along +X with <see cref="SkySettings.WindSpeed"/>.
/// </summary>
public sealed class CloudLayer : IDisposable
{
    public const int   Cells     = 64;
    public const float CellSize  = 12f;
    public const float Thickness = 4f;
    public const float TileSize  = Cells * CellSize;

    /// <summary>Cloud fog, blocks from the camera: fully faded before the edge of the 3×3 tiles (at least one tile
    /// away in every direction), so the layer never visibly ends.</summary>
    public const float FogStart = 0.35f * TileSize;
    public const float FogEnd   = 0.95f * TileSize;

    private const int Seed = 0x5EED;

    private static readonly Vector3D<float> CloudColor = new(0.97f, 0.98f, 1.0f);

    private readonly Renderer _renderer;
    private GpuMesh? _mesh;
    private float _builtCoverage = -1f;
    private readonly Mat4[] _tiles = new Mat4[9];

    public CloudLayer(Renderer renderer) => _renderer = renderer;

    /// <summary>Draws the layer around <paramref name="cameraPos"/>; <paramref name="timeSeconds"/> drives the drift.</summary>
    public void Draw(Vector3D<float> cameraPos, double timeSeconds)
    {
        if (_mesh == null || _builtCoverage != SkySettings.CloudCoverage) Rebuild(SkySettings.CloudCoverage);
        if (_mesh == null) return; // no cloud cells at this coverage

        // Drift wrapped to one tile in double, so the offset stays precise however long the game runs.
        float drift = (float)((timeSeconds * SkySettings.WindSpeed) % TileSize);
        int ci = (int)MathF.Floor((cameraPos.X - drift) / TileSize);
        int cj = (int)MathF.Floor(cameraPos.Z / TileSize);

        int k = 0;
        for (int i = -1; i <= 1; i++)
        for (int j = -1; j <= 1; j++)
            _tiles[k++] = Mat4.Translation(new Vector3D<float>(
                (ci + i) * TileSize + drift, SkySettings.CloudAltitude, (cj + j) * TileSize));
        _renderer.DrawClouds(_mesh, _tiles);
    }

    private void Rebuild(float coverage)
    {
        _mesh?.Dispose();
        _mesh = null;
        _builtCoverage = coverage;

        var cloud = BuildPattern(coverage);
        var verts = new List<Vertex>();
        var idx   = new List<uint>();
        for (int z = 0; z < Cells; z++)
        for (int x = 0; x < Cells; x++)
        {
            if (!cloud[x, z]) continue;
            var min = new Vector3D<float>(x * CellSize, 0f, z * CellSize);
            var max = min + new Vector3D<float>(CellSize, Thickness, CellSize);
            var ex = new Vector3D<float>(CellSize, 0, 0);
            var ey = new Vector3D<float>(0, Thickness, 0);
            var ez = new Vector3D<float>(0, 0, CellSize);

            // Tops and bottoms always; sides only where the (wrapped) neighbour cell is empty, so a cloud of many
            // cells reads as one solid shape. Each quad's two edges are ordered so edge1 × edge2 is the outward
            // normal, which is counter-clockwise from outside (the front face).
            AddQuad(verts, idx, new(min.X, max.Y, min.Z), ez, ex, new(0, 1, 0));
            AddQuad(verts, idx, min, ex, ez, new(0, -1, 0));
            if (!cloud[(x + 1) % Cells, z])         AddQuad(verts, idx, new(max.X, min.Y, min.Z), ey, ez, new(1, 0, 0));
            if (!cloud[(x + Cells - 1) % Cells, z]) AddQuad(verts, idx, min, ez, ey, new(-1, 0, 0));
            if (!cloud[x, (z + 1) % Cells])         AddQuad(verts, idx, new(min.X, min.Y, max.Z), ex, ey, new(0, 0, 1));
            if (!cloud[x, (z + Cells - 1) % Cells]) AddQuad(verts, idx, min, ey, ex, new(0, 0, -1));
        }
        if (idx.Count > 0) _mesh = _renderer.UploadMesh(verts.ToArray(), idx.ToArray());
    }

    private static void AddQuad(List<Vertex> verts, List<uint> idx, Vector3D<float> origin,
                                Vector3D<float> e1, Vector3D<float> e2, Vector3D<float> normal)
    {
        uint b = (uint)verts.Count;
        verts.Add(new Vertex(origin,           normal, CloudColor));
        verts.Add(new Vertex(origin + e1,      normal, CloudColor));
        verts.Add(new Vertex(origin + e1 + e2, normal, CloudColor));
        verts.Add(new Vertex(origin + e2,      normal, CloudColor));
        idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
        idx.Add(b); idx.Add(b + 2); idx.Add(b + 3);
    }

    /// <summary>Which cells are cloud: three octaves of wrapping value noise (lattice spacings 16, 8 and 4 cells, so
    /// the tile repeats seamlessly), thresholded at the quantile that makes exactly <paramref name="coverage"/> of the
    /// cells cloud.</summary>
    private static bool[,] BuildPattern(float coverage)
    {
        var value = new float[Cells, Cells];
        (int spacing, float weight)[] octaves = { (16, 0.55f), (8, 0.3f), (4, 0.15f) };
        var rng = new Random(Seed);
        foreach (var (spacing, weight) in octaves)
        {
            int n = Cells / spacing;
            var lattice = new float[n, n];
            for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
                lattice[a, b] = rng.NextSingle();

            for (int z = 0; z < Cells; z++)
            for (int x = 0; x < Cells; x++)
            {
                float fx = (x + 0.5f) / spacing, fz = (z + 0.5f) / spacing;
                int x0 = (int)MathF.Floor(fx), z0 = (int)MathF.Floor(fz);
                float tx = Smooth(fx - x0), tz = Smooth(fz - z0);
                float v00 = lattice[x0 % n, z0 % n],             v10 = lattice[(x0 + 1) % n, z0 % n];
                float v01 = lattice[x0 % n, (z0 + 1) % n],       v11 = lattice[(x0 + 1) % n, (z0 + 1) % n];
                float v = Lerp(Lerp(v00, v10, tx), Lerp(v01, v11, tx), tz);
                value[x, z] += weight * v;
            }
        }

        var sorted = new float[Cells * Cells];
        Buffer.BlockCopy(value, 0, sorted, 0, sorted.Length * sizeof(float));
        Array.Sort(sorted);
        int cloudCells = (int)MathF.Round(System.Math.Clamp(coverage, 0f, 1f) * sorted.Length);
        float threshold = cloudCells == 0 ? float.MaxValue : sorted[sorted.Length - cloudCells];

        var cloud = new bool[Cells, Cells];
        for (int z = 0; z < Cells; z++)
        for (int x = 0; x < Cells; x++)
            cloud[x, z] = value[x, z] >= threshold;
        return cloud;
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public void Dispose() => _mesh?.Dispose();
}
