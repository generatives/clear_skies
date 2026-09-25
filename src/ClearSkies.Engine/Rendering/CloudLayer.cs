using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Minecraft-style blocky clouds in three stacked layers above the islands (<see cref="Levels"/>), drawn out to <see cref="Distance"/> blocks as real depth-tested boxes so islands and ships sort
/// against them properly.
///
/// Each layer is a grid of square cells; a cell is either empty or one box, whose underside wanders up and down with a
/// broad noise field (so clouds sit at different heights) and whose thickness grows towards the middle of each cloud.
/// How much of the sky is cloud blends between <see cref="SkySettings.CloudCoverageOpen"/> and
/// <see cref="SkySettings.CloudCoverageIslands"/> by an <see cref="ICloudDensityMap"/>, so clouds bank up around
/// islands and thin out over empty sky. Each layer up falls off more steeply away from an island (and is sparser over
/// open sky), so the layers shrink towards the island as they rise and islands wear a pile of cloud.
///
/// Every layer drifts along +X with the wind at its own speed. Cells are generated in each layer's drifting frame, in
/// tiles of <see cref="TileCells"/>² cells built on worker threads and drawn as one instanced draw each (one instance
/// per cloud cell, see <see cref="CloudCell"/>). The density map is fixed to the world, though, so a tile is rebuilt
/// once its layer has drifted <see cref="RebuildDrift"/> blocks since it was built: clouds form upwind of an island
/// and thin out downwind of it a few cells at a time.
/// </summary>
public sealed class CloudLayer : IDisposable
{
    /// <summary>How far clouds are drawn, in blocks from the camera. <see cref="Camera.FarPlane"/> reaches past it.</summary>
    public const float Distance = 12000f;

    /// <summary>Cloud fog, blocks from the camera: faded out entirely by <see cref="Distance"/>, so the edge never shows.</summary>
    public const float FogStart = 2500f;
    public const float FogEnd   = Distance;

    public const int TileCells = 256; // cell coordinates in a tile fit a byte (see CloudCell)

    private const float CellSize     = 16f; // blocks across a cell
    private const float TileSize     = TileCells * CellSize;
    private const float MinThickness = 4f;  // blocks, at a cloud's edge
    private const int   DensityStep  = 8;   // cells between density-map samples (bilinear in between)
    private const float RebuildDrift = 96f; // blocks a layer drifts before its tiles are rebuilt against the density map
    private const int   MaxBuilding  = 4;   // tile builds in flight at once

    /// <summary>One cloud layer. Heights are blocks relative to <see cref="SkySettings.CloudAltitude"/>
    /// + <see cref="AltitudeOffset"/>; <see cref="Spacing"/> is the noise's largest lattice spacing in cells (roughly
    /// the size of one cloud). Coverage is <see cref="SkySettings.CloudCoverageOpen"/> × <see cref="OpenScale"/> in
    /// open sky, rising to <see cref="SkySettings.CloudCoverageIslands"/> × <see cref="IslandScale"/> by the density
    /// map raised to <see cref="Falloff"/>: the higher it is, the tighter the layer hugs the island.</summary>
    private sealed record Level(float AltitudeOffset, float MaxThickness, float BottomVariation, float OpenScale,
                                float IslandScale, float Falloff, float WindScale, uint Seed, int Spacing);

    private static readonly Level[] Levels =
    {
        // altitude thickness wander open island falloff wind  seed     spacing
        new(   0f,    28f,    24f,  1.0f, 1.0f,  1f,    1.0f, 0x1C10D, 16),
        new(  70f,    24f,    20f,  0.5f, 0.9f,  2.5f,  1.1f, 0x2C10D, 14),
        new( 140f,    20f,    16f,  0.2f, 0.8f,  5f,    1.2f, 0x3C10D, 12),
    };

    /// <summary>One cloud cell (a box), as the cloud shader's instance data. <see cref="Packed"/>: bits 0-7 cell x
    /// and 8-15 cell z within the tile. <see cref="Heights"/>: bottom (low 16 bits) and top (high 16 bits) as signed
    /// blocks above the layer's altitude.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CloudCell
    {
        public uint Packed;
        public uint Heights;
    }

    private sealed class Tile
    {
        public GpuBuffer? Buffer;
        public uint Count;
        public bool Built, Building;
        public double BuiltDrift;
        public int BuiltVersion;
    }

    private readonly record struct Built((int Level, int X, int Z) Key, List<CloudCell> Cells, double Drift, int Version);

    private readonly Renderer _renderer;
    private readonly ICloudDensityMap? _density;
    private readonly Dictionary<(int Level, int X, int Z), Tile> _tiles = new();
    private readonly ConcurrentQueue<Built> _built = new();
    private readonly List<(int Level, int X, int Z)> _stale = new();
    private readonly HashSet<(int Level, int X, int Z)> _inRange = new();
    private readonly List<CloudTileDraw> _draws = new();
    private readonly double[] _drift = new double[Levels.Length];
    private double _lastTime = double.NaN;
    private int _building;
    private int _version;
    private (float Open, float Islands) _builtCoverage = (-1f, -1f);

    public CloudLayer(Renderer renderer, ICloudDensityMap? density = null)
    {
        _renderer = renderer;
        _density = density;
    }

    public int TileCount => _tiles.Count;
    public long CellCount { get; private set; }

    /// <summary>Draws the layers around <paramref name="cameraPos"/>; <paramref name="timeSeconds"/> drives the drift.</summary>
    public void Draw(Vector3D<float> cameraPos, double timeSeconds)
    {
        // Integrate the drift (rather than time * speed), so changing the wind speed doesn't jump the clouds.
        double dt = double.IsNaN(_lastTime) ? 0 : System.Math.Clamp(timeSeconds - _lastTime, 0, 1);
        _lastTime = timeSeconds;
        for (int l = 0; l < Levels.Length; l++) _drift[l] += dt * SkySettings.WindSpeed * Levels[l].WindScale;

        var coverage = (SkySettings.CloudCoverageOpen, SkySettings.CloudCoverageIslands);
        if (coverage != _builtCoverage) { _builtCoverage = coverage; _version++; }

        while (_built.TryDequeue(out var b))
        {
            _building--;
            if (!_tiles.TryGetValue(b.Key, out var tile)) continue; // went out of range while building
            tile.Buffer?.Dispose();
            tile.Buffer = b.Cells.Count > 0 ? _renderer.UploadInstances<CloudCell>(CollectionsMarshal.AsSpan(b.Cells)) : null;
            tile.Count = (uint)b.Cells.Count;
            tile.Built = true; tile.Building = false;
            tile.BuiltDrift = b.Drift; tile.BuiltVersion = b.Version;
        }

        _draws.Clear();
        _stale.Clear();
        long cells = 0;
        _inRange.Clear();
        for (int l = 0; l < Levels.Length; l++)
        {
            double camX = cameraPos.X - _drift[l]; // the camera in this layer's drifting frame
            double camZ = cameraPos.Z;
            int x0 = (int)System.Math.Floor((camX - Distance) / TileSize), x1 = (int)System.Math.Floor((camX + Distance) / TileSize);
            int z0 = (int)System.Math.Floor((camZ - Distance) / TileSize), z1 = (int)System.Math.Floor((camZ + Distance) / TileSize);
            float altitude = SkySettings.CloudAltitude + Levels[l].AltitudeOffset;
            for (int tz = z0; tz <= z1; tz++)
            for (int tx = x0; tx <= x1; tx++)
            {
                // Nearest point of the tile to the camera, horizontally.
                double dx = System.Math.Max(0, System.Math.Max(tx * TileSize - camX, camX - (tx + 1) * TileSize));
                double dz = System.Math.Max(0, System.Math.Max(tz * TileSize - camZ, camZ - (tz + 1) * TileSize));
                if (dx * dx + dz * dz > (double)Distance * Distance) continue;

                var key = (l, tx, tz);
                _inRange.Add(key);
                if (!_tiles.TryGetValue(key, out var tile)) _tiles[key] = tile = new Tile();
                if (!tile.Building && (!tile.Built || tile.BuiltVersion != _version
                                       || System.Math.Abs(_drift[l] - tile.BuiltDrift) > RebuildDrift))
                    _stale.Add(key);

                if (tile.Buffer == null) continue;
                cells += tile.Count;
                var model = Mat4.Scale(new Vector3D<float>(CellSize, 1f, CellSize));
                model.M12 = (float)(tx * (double)TileSize + _drift[l]);
                model.M13 = altitude;
                model.M14 = tz * TileSize;
                _draws.Add(new CloudTileDraw(tile.Buffer, tile.Count, model));
            }
        }
        CellCount = cells;

        foreach (var key in _tiles.Keys.Where(k => !_inRange.Contains(k)).ToList())
        {
            _tiles[key].Buffer?.Dispose();
            _tiles.Remove(key);
        }

        // Tiles never built first (holes in the sky), then the nearest.
        if (_stale.Count > 0 && _building < MaxBuilding)
        {
            _stale.Sort((a, b) =>
            {
                int built = _tiles[a].Built.CompareTo(_tiles[b].Built);
                return built != 0 ? built : TileDistance(a, cameraPos).CompareTo(TileDistance(b, cameraPos));
            });
            foreach (var key in _stale)
            {
                if (_building >= MaxBuilding) break;
                StartBuild(key);
            }
        }

        _renderer.DrawClouds(_draws);
    }

    private double TileDistance((int Level, int X, int Z) key, Vector3D<float> cameraPos)
    {
        double cx = (key.X + 0.5) * TileSize + _drift[key.Level] - cameraPos.X;
        double cz = (key.Z + 0.5) * TileSize - cameraPos.Z;
        return cx * cx + cz * cz;
    }

    private void StartBuild((int Level, int X, int Z) key)
    {
        _tiles[key].Building = true;
        _building++;
        var lv = Levels[key.Level];
        double drift = _drift[key.Level];
        int version = _version;
        var (open, islands) = _builtCoverage;
        var density = _density;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            List<CloudCell> cells;
            try { cells = BuildTile(lv, key.X, key.Z, drift, open, islands, density); }
            catch (Exception e)
            {
                Console.WriteLine($"[clouds] tile {key} failed: {e}");
                cells = new List<CloudCell>();
            }
            _built.Enqueue(new Built(key, cells, drift, version));
        });
    }

    // ── Tile generation (worker threads) ─────────────────────────────────────

    private static List<CloudCell> BuildTile(Level lv, int tx, int tz, double drift, float open, float islands,
                                             ICloudDensityMap? density)
    {
        int ox = tx * TileCells, oz = tz * TileCells; // the tile's first cell, in the layer's cell coordinates

        // The density map, sampled every DensityStep cells at where those cells are over the world right now.
        const int n = TileCells / DensityStep + 1;
        var dens = new float[n * n];
        if (density != null)
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                dens[i + j * n] = System.Math.Clamp(density.Density(
                    (float)((ox + i * DensityStep) * (double)CellSize + drift),
                    (oz + j * DensityStep) * CellSize), 0f, 1f);

        var cells = new List<CloudCell>();
        for (int z = 0; z < TileCells; z++)
        for (int x = 0; x < TileCells; x++)
        {
            float d = MathF.Pow(SampleDensity(dens, n, x, z), lv.Falloff);
            float openCov = open * lv.OpenScale;
            float coverage = System.Math.Clamp(openCov + (islands * lv.IslandScale - openCov) * d, 0f, 1f);
            if (coverage <= 0f) continue;
            int gx = ox + x, gz = oz + z;
            float v = Fbm(gx, gz, lv.Spacing, lv.Seed);
            float threshold = Threshold(coverage);
            if (v < threshold) continue;

            // Thicker towards the middle of a cloud (further above the threshold), in 2-block steps; the underside
            // bulges down a little under the thick middle. The base height wanders with a broad field.
            float e = System.Math.Clamp((v - threshold) / 0.12f, 0f, 1f);
            int thickness = 2 * (int)MathF.Round((MinThickness + (lv.MaxThickness - MinThickness) * e) * 0.5f);
            float wander = (ValueNoise(gx, gz, lv.Spacing * 5, lv.Seed ^ 0xB077u) * 2f - 1f) * lv.BottomVariation;
            int baseY = 4 * (int)MathF.Round(wander * 0.25f);
            int sag = 2 * (int)(thickness * 0.15f);
            short bottom = (short)(baseY - sag), top = (short)(baseY + thickness - sag);
            cells.Add(new CloudCell
            {
                Packed  = (uint)x | ((uint)z << 8),
                Heights = (ushort)bottom | ((uint)(ushort)top << 16),
            });
        }
        return cells;
    }

    private static float SampleDensity(float[] dens, int n, int x, int z)
    {
        float fx = x / (float)DensityStep, fz = z / (float)DensityStep;
        int i = System.Math.Min((int)fx, n - 2), j = System.Math.Min((int)fz, n - 2);
        float u = fx - i, w = fz - j;
        float a = Lerp(dens[i + j * n], dens[i + 1 + j * n], u);
        float c = Lerp(dens[i + (j + 1) * n], dens[i + 1 + (j + 1) * n], u);
        return Lerp(a, c, w);
    }

    // ── Noise ────────────────────────────────────────────────────────────────

    /// <summary>Three octaves of value noise at lattice spacings of <paramref name="spacing"/>, half and a quarter of
    /// it (in cells), weighted 0.55 / 0.3 / 0.15 so the result stays in [0, 1].</summary>
    private static float Fbm(int gx, int gz, int spacing, uint seed)
        => 0.55f * ValueNoise(gx, gz, spacing, seed)
         + 0.30f * ValueNoise(gx, gz, System.Math.Max(1, spacing / 2), seed + 1)
         + 0.15f * ValueNoise(gx, gz, System.Math.Max(1, spacing / 4), seed + 2);

    private static float ValueNoise(int gx, int gz, int spacing, uint seed)
    {
        float fx = (gx + 0.5f) / spacing, fz = (gz + 0.5f) / spacing;
        float flx = MathF.Floor(fx), flz = MathF.Floor(fz);
        int x0 = (int)flx, z0 = (int)flz;
        float tx = Smooth(fx - flx), tz = Smooth(fz - flz);
        float v00 = Hash01(x0, z0, seed),     v10 = Hash01(x0 + 1, z0, seed);
        float v01 = Hash01(x0, z0 + 1, seed), v11 = Hash01(x0 + 1, z0 + 1, seed);
        return Lerp(Lerp(v00, v10, tx), Lerp(v01, v11, tx), tz);
    }

    private static float Hash01(int x, int z, uint seed)
    {
        uint h = (uint)x * 0x8DA6B343u ^ (uint)z * 0xD8163841u ^ seed * 0xCB1AB31Fu;
        h ^= h >> 16; h *= 0x7FEB352Du;
        h ^= h >> 15; h *= 0x846CA68Bu;
        h ^= h >> 16;
        return (h >> 8) * (1f / (1 << 24));
    }

    /// <summary><see cref="Fbm"/>'s distribution, sampled once: turns a coverage fraction into the noise threshold
    /// above which that fraction of cells is cloud.</summary>
    private static readonly float[] FbmQuantiles = BuildQuantiles();

    private static float[] BuildQuantiles()
    {
        const int side = 128;
        var v = new float[side * side];
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
            v[x + z * side] = Fbm(x * 7, z * 7, 16, 0x51A7u);
        Array.Sort(v);
        return v;
    }

    private static float Threshold(float coverage)
    {
        int i = (int)((1f - coverage) * (FbmQuantiles.Length - 1));
        return FbmQuantiles[System.Math.Clamp(i, 0, FbmQuantiles.Length - 1)];
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public void Dispose()
    {
        foreach (var t in _tiles.Values) t.Buffer?.Dispose();
        _tiles.Clear();
    }
}

/// <summary>One instanced draw of a <see cref="CloudLayer"/> tile: <paramref name="Count"/> <see cref="CloudLayer.CloudCell"/>s
/// from <paramref name="Instances"/>, placed by <paramref name="Model"/> (cell units to world).</summary>
public readonly record struct CloudTileDraw(GpuBuffer Instances, uint Count, Mat4 Model);
