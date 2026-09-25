using System.Collections.Concurrent;

namespace ClearSkies.Game.Generation;

/// <summary>The kinds of hearts, each with its own grid (see <see cref="HeartGrid"/>). Fragments are packed tightly in
/// clusters (<see cref="HeartGrid.ClusterField"/>), a few dozen blocks apart: the terrain broken into pieces, its shape
/// still readable across them. Medium and small hearts are outlying islands around the clusters, and on chains.</summary>
public enum HeartKind { Fragment, Medium, Small }

/// <summary>How a heart's support is shaped, and so the island it holds.</summary>
public enum SupportShape
{
    /// <summary>A rounded lens: soft edges, a bowl underneath.</summary>
    Lens,
    /// <summary>A faceted outline, some of whose sides are cliffs (a vertical band of <see cref="Heart.Band"/> below the
    /// heart, then curving under, so the faces show the terrain's rock layers) and the rest rounded slopes, with a cone
    /// underneath.</summary>
    Cliff,
    /// <summary>A narrower cliff shape, more of its sides cliffs, with a long spike underneath.</summary>
    Spire,
}

/// <summary>
/// One island heart and the support around it: the part of the <see cref="ContinentTerrain"/> inside the support is the
/// island the heart holds up. Positions are world blocks. Hearts are anywhere in the ground; each holds up a chunk of it,
/// for each column one height span (<see cref="Span"/>) up to a rolling top about <see cref="Up"/> above the heart. Where
/// the terrain surface is lower than that, the terrain is the island's top (grass, hills, peaks); elsewhere the top is
/// the support's own, mostly flat.
/// </summary>
public struct Heart
{
    public HeartKind Kind;
    public SupportShape Shape;
    public float X, Y, Z;
    public float Radius;
    public float Up;          // height of the support's top above the heart
    public float Roll;        // how far the top rolls up and down
    public float Wall;        // lens: underside depth below the heart; cliff/spire: depth of the walls below it
    public float Spike;       // cliff/spire: depth of the cone below the walls
    public float SpikePower;  // cliff/spire: the cone's profile ((1 - t)^power: under 1 bulges, over 1 is pointed)
    public float Band;        // cliff/spire: how far a cliff side's vertical face goes below the heart
    public float Rotation;    // cliff/spire: the outline's rotation
    public int Sides;         // cliff/spire: the outline's corners
    public uint Id;           // per-heart noise (outline corners, which sides are cliffs, lens edge wobble, the top)

    /// <summary>The farthest the support reaches from the heart, horizontally.</summary>
    public readonly float Reach => Radius * 1.3f;

    public readonly float YMin => Y - MaxLump * (Shape == SupportShape.Lens ? Wall : MathF.Max(Wall + Spike, Band)) - 2f;

    /// <summary>The most the underside's lumps deepen it (see <see cref="Span"/>).</summary>
    private const float MaxLump = 1.3f;
    public readonly float YMax => Y + Up + Roll + 1f;

    /// <summary>The support's span at column (x, z), if the column is inside it.</summary>
    public readonly bool Span(float x, float z, out float bottom, out float top)
    {
        float dx = x - X, dz = z - Z;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        bottom = top = 0f;
        if (d >= Reach) return false;

        // t: 0 at the heart, 1 at the edge. cliff: 1 along a cliff side (straight down, full height to the edge), 0
        // along a rounded one.
        float t, cliff, depth;
        if (Shape == SupportShape.Lens)
        {
            // Wobbly round outline; underside a bowl, (1 - t²)^0.75, rounded at the rim.
            float wobble = HeartGrid.ValueNoise(Id, x / (Radius * 0.5f), z / (Radius * 0.5f));
            t = d / (Radius * (0.8f + 0.4f * wobble) * Ragged(x, z));
            if (t >= 1f) return false;
            cliff = 0f;
            depth = Wall * MathF.Pow(1f - t * t, 0.75f);
        }
        else
        {
            // A polygon of Sides corners at radii between 0.7 and 1.15 of the radius: straight faces with corners.
            // Underneath, walls rounding off towards the edge like a lens, and a cone of Spike. Along a cliff side the
            // support goes at least Band straight down at the edge, so the face is a vertical band that then curves
            // under; cliff-ness blends over the last part of a side into the next.
            float angle = MathF.Atan2(dz, dx) - Rotation;
            float sector = MathF.Tau / Sides;
            float a = (angle % MathF.Tau + MathF.Tau) % MathF.Tau / sector;
            int i = (int)a;
            float phi = (a - i) * sector;
            float ri = Corner(i % Sides), rj = Corner((i + 1) % Sides);
            float edge = ri * rj * MathF.Sin(sector) / (ri * MathF.Sin(phi) + rj * MathF.Sin(sector - phi));
            t = d / (edge * Ragged(x, z));
            if (t >= 1f) return false;
            float f = phi / sector;
            cliff = IsCliff(i);
            if (f < 0.15f) cliff = Lerp(0.5f * (IsCliff(i - 1) + cliff), cliff, f / 0.15f);
            else if (f > 0.85f) cliff = Lerp(cliff, 0.5f * (cliff + IsCliff(i + 1)), (f - 0.85f) / 0.15f);
            depth = Wall * MathF.Pow(1f - t * t, 0.75f) + Spike * MathF.Pow(1f - t, SpikePower);
            // The band's foot is ragged, not ruled.
            float band = Band * (0.55f + 0.75f * HeartGrid.ValueNoise(Id ^ 0xBA5Eu, x / 18f, z / 18f));
            depth = MathF.Max(depth, cliff * band);
        }

        // A lumpy underside: bulges and hollows a dozen blocks or so across.
        depth *= 0.7f + 0.6f * HeartGrid.ValueNoise(Id ^ 0xB0B0u, x / 14f, z / 14f);
        bottom = Y - depth;

        // The top: a broad roll plus finer bumps, dropping off towards a rounded edge (not a cliff one).
        float broad = HeartGrid.ValueNoise(Id ^ 0x70Fu, x / (Radius * 0.35f), z / (Radius * 0.35f));
        float fine = HeartGrid.ValueNoise(Id ^ 0x70Eu, x / 24f, z / 24f);
        float drop = Up * 0.6f * Smoothstep(0.7f, 1f, t) * (1f - cliff);
        top = Y + Up + Roll * (1.4f * broad + 0.6f * fine - 1f) - drop;
        return top > bottom + 1f;
    }

    /// <summary>0.9-1.08: how far the outline at (x, z) is pushed in or out, so the sides have notches, buttresses and
    /// spurs rather than smooth faces.</summary>
    private readonly float Ragged(float x, float z)
        => 0.9f + 0.1f * HeartGrid.ValueNoise(Id ^ 0x5EEDu, x / 30f, z / 30f)
                + 0.08f * HeartGrid.ValueNoise(Id ^ 0x5EEEu, x / 9f, z / 9f);

    // Corners at 0.7-1.15 of the radius: with the outline's raggedness, still inside Reach.
    private readonly float Corner(int i) => Radius * (0.7f + 0.45f * HeartGrid.Hash01(Id, i, 0));

    /// <summary>1 if side i (from corner i to the next) is a cliff, 0 if it slopes: most of a spire's sides are cliffs,
    /// about 40% of a cliff's.</summary>
    private readonly float IsCliff(int i)
        => HeartGrid.Hash01(Id, ((i % Sides) + Sides) % Sides, 1) < (Shape == SupportShape.Spire ? 0.7f : 0.4f) ? 1f : 0f;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>
/// Where island hearts are. The world is a <see cref="ContinentTerrain"/> with hearts scattered through it; each heart
/// holds up the terrain inside its support, and nothing else exists. Each <see cref="HeartKind"/> has its own grid of
/// 3D cells, at most one heart per cell, anywhere in the ground (cells above the terrain have none). Hearts are as
/// likely anywhere in the ground, and there is far more ground low down than up in the peaks, so islands are dense near
/// the bottom of the world and rare near the top.
///
/// Where hearts are is coherent rather than even: they gather in clusters (the <see cref="Clumps"/> field) with open sky
/// between, and the smaller ones also along narrow winding bands (the <see cref="Chains"/> field). A support may reach
/// past its own cell (up to its kind's greatest reach), so a column's hearts are found in the cells within that reach.
/// </summary>
public static class HeartGrid
{
    /// <summary>One kind's grid: cells of <see cref="CellSize"/> blocks across and <see cref="CellHeight"/> tall, a heart's radius, and the chance of a heart outside clusters and inside them.</summary>
    private readonly record struct KindDef(int CellSize, int CellHeight, float MinRadius, float MaxRadius,
                                           float ChanceLow, float ChanceHigh);

    private static readonly KindDef[] Kinds =
    {
        //   cell height  radius        chance: outside, inside clusters
        new( 128,  120,   38f,  100f, 0f,     0.35f), // Fragment: clusters only (radius: see FragmentMin)
        new( 600,  300,   80f,  220f, 0.03f,  0.50f), // Medium
        new( 320,  200,   30f,   90f, 0.01f,  0.30f), // Small
    };

    /// <summary>A fragment's radius as a share of its cell: most leave gaps of a few dozen blocks to their neighbours
    /// (chasms through the cluster); some are bigger and merge with them into larger pieces.</summary>
    private const float FragmentMin = 0.3f, FragmentMax = 0.45f, BigFragmentMin = 0.55f, BigFragmentMax = 0.78f,
                        BigFragmentChance = 0.2f;

    /// <summary>Clusters: blobs of value noise at this spacing, their edges roughened by a finer one, gathered where the
    /// big <see cref="Clumps"/> field is.</summary>
    private const float ClusterSpacing = 1400f, ClusterDetail = 450f;

    public const int KindCount = 3;

    /// <summary>How far under the terrain surface (at its middle) a heart must be.</summary>
    private const float HeartCover = 5f;

    /// <summary>The lowest an island may reach: above the hearts world's cloud sea (<see cref="CloudSeaAltitude"/>).</summary>
    internal const float LowestBottom = IslandGrid.WorldBottom + 56f;

    /// <summary>The hearts world's cloud sea (see SkySettings.CloudSeaAltitude): its layer ends a good way below the
    /// lowest islands, so none sits in it.</summary>
    public const float CloudSeaAltitude = -300f;

    // Clumps: value noise at these spacings (blocks); chains: where another value noise crosses its middle, within
    // ChainWidth of it.
    private const float ClumpSpacing = 9000f, ClumpDetail = 3000f, ChainSpacing = 4000f, ChainWidth = 0.04f;

    /// <summary>Writes the hearts of kind <paramref name="k"/> whose cells are within the kind's greatest reach of the
    /// box (at any height) into <paramref name="output"/> starting at <paramref name="count"/>, and returns the new
    /// count: every heart whose support can touch the box's columns, and some that can't.</summary>
    public static int Collect(ulong seed, HeartKind k, float minX, float minZ, float maxX, float maxZ,
                              Span<Heart> output, int count)
    {
        var def = Kinds[(int)k];
        float pad = def.MaxRadius * 1.2f;
        int x0 = FloorDiv(minX - pad, def.CellSize), x1 = FloorDiv(maxX + pad, def.CellSize);
        int z0 = FloorDiv(minZ - pad, def.CellSize), z1 = FloorDiv(maxZ + pad, def.CellSize);
        int y0 = FloorDiv(LowestBottom, def.CellHeight), y1 = FloorDiv(IslandGrid.WorldTop, def.CellHeight);
        for (int cz = z0; cz <= z1; cz++)
        for (int cx = x0; cx <= x1; cx++)
        for (int cy = y0; cy <= y1; cy++)
        {
            if (count == output.Length) return count;
            if (Placed(seed, k, cx, cy, cz, out output[count])) count++;
        }
        return count;
    }

    /// <summary>All kinds' hearts near the box (see <see cref="Collect"/>).</summary>
    public static int CollectAll(ulong seed, float minX, float minZ, float maxX, float maxZ, Span<Heart> output)
    {
        int count = 0;
        for (int k = 0; k < KindCount; k++) count = Collect(seed, (HeartKind)k, minX, minZ, maxX, maxZ, output, count);
        return count;
    }

    public static bool Placed(ulong seed, HeartKind k, int cx, int cy, int cz, out Heart heart)
    {
        var key = (seed, (int)k, cx, cy, cz);
        if (!Cache.TryGetValue(key, out var v))
        {
            v = (TryPlace(seed, k, cx, cy, cz, out var h), h);
            if (Cache.Count > CacheLimit) Cache.Clear();
            Cache[key] = v;
        }
        heart = v.Item2;
        return v.Item1;
    }

    private const int CacheLimit = 1 << 21;
    private static readonly ConcurrentDictionary<(ulong, int, int, int, int), (bool, Heart)> Cache = new();

    private static bool TryPlace(ulong seed, HeartKind k, int cx, int cy, int cz, out Heart heart)
    {
        heart = default;
        var def = Kinds[(int)k];
        ulong hash = HashCell(seed, 100 + (int)k, cx, cy, cz);
        var rng = new SplitMix64Rng(hash);
        float roll = rng.NextFloat01();
        if (roll >= def.ChanceHigh) return false; // cheap reject

        bool fragment = k == HeartKind.Fragment;
        float radius = fragment
            ? def.CellSize * (rng.NextFloat01() < BigFragmentChance ? rng.NextRange(BigFragmentMin, BigFragmentMax)
                                                                    : rng.NextRange(FragmentMin, FragmentMax))
            : Lerp(def.MinRadius, def.MaxRadius, MathF.Pow(rng.NextFloat01(), 1.5f));
        // A fragment keeps off its cell's edges, so neighbours don't crowd into one another.
        float margin = fragment ? 0.2f : 0f;
        float x = (cx + rng.NextRange(margin, 1f - margin)) * def.CellSize;
        float z = (cz + rng.NextRange(margin, 1f - margin)) * def.CellSize;
        // Fragments fill the clusters; the other hearts gather over a wider area around them, and on chains.
        float bias = fragment ? ClusterField(seed, x, z) : MathF.Max(Clumps(seed, x, z, 0.42f, 0.6f), 0.8f * Chains(seed, x, z));
        float chance = Lerp(def.ChanceLow, def.ChanceHigh, bias);
        if (roll >= chance) return false;

        float shapeRoll = rng.NextFloat01();
        var shape = fragment ? (shapeRoll < 0.35f ? SupportShape.Lens : shapeRoll < 0.9f ? SupportShape.Cliff : SupportShape.Spire)
                  : shapeRoll < 0.55f ? SupportShape.Lens : shapeRoll < 0.9f ? SupportShape.Cliff : SupportShape.Spire;
        if (shape == SupportShape.Spire) radius *= 0.6f;
        heart = new Heart
        {
            Kind = k, Shape = shape, X = x, Z = z, Id = (uint)hash, Radius = radius,
            Rotation = rng.NextFloat01() * MathF.Tau,
            Sides = 5 + (int)(rng.NextFloat01() * 5f),
        };

        // Underside: a lens's depth, or a cliff's walls and cone, by the radius.
        float flat = fragment ? 0.7f : 0.85f;
        switch (shape)
        {
            case SupportShape.Lens:
                heart.Wall = flat * radius * rng.NextRange(0.35f, 0.6f);
                break;
            case SupportShape.Cliff:
                heart.Wall = flat * radius * rng.NextRange(0.2f, 0.4f);
                heart.Spike = flat * radius * rng.NextRange(0.3f, 0.7f);
                heart.SpikePower = rng.NextRange(0.6f, 1.6f);
                heart.Band = MathF.Min(rng.NextRange(40f, 120f), radius * 0.4f);
                break;
            default:
                heart.Wall = radius * rng.NextRange(0.15f, 0.3f);
                heart.Spike = radius * rng.NextRange(0.8f, 1.3f);
                heart.SpikePower = rng.NextRange(0.7f, 1.2f);
                heart.Band = MathF.Min(rng.NextRange(40f, 120f), radius * 0.5f);
                break;
        }

        // Anywhere in its cell, as long as it is in the ground. Its top may reach above the terrain, which is then the
        // island's top.
        heart.Y = (cy + rng.NextFloat01()) * def.CellHeight;
        if (heart.Y > ContinentTerrain.For(seed).Height(x, z) - HeartCover) return false;
        if (roll >= chance * HeightDensity(heart.Y)) return false;
        heart.Up = fragment ? rng.NextRange(12f, 30f) + radius * 0.15f : rng.NextRange(20f, 50f) + radius * 0.15f;
        heart.Roll = rng.NextRange(5f, 12f) + radius * 0.06f;

        // Too deep for the world: trim the cone, then the walls, rather than lose the island.
        float over = LowestBottom - heart.YMin;
        if (over > 0f && shape != SupportShape.Lens)
        {
            float trim = MathF.Min(over, heart.Spike);
            heart.Spike -= trim; over -= trim;
            heart.Wall -= MathF.Min(over, MathF.Max(heart.Wall - 40f, 0f));
            heart.Band = MathF.Min(heart.Band, heart.Y - LowestBottom - 2f);
        }
        return heart.YMin >= LowestBottom;
    }

    /// <summary>0-1: how far inside a cluster (x, z) is: 0 below <paramref name="lo"/> of the cluster field, 1 above
    /// <paramref name="hi"/> (a lower range reaches wider).</summary>
    public static float Clumps(ulong seed, float x, float z, float lo = 0.52f, float hi = 0.68f)
    {
        float v = 0.7f * ValueNoise((uint)seed ^ 0xC1u, x / ClumpSpacing, z / ClumpSpacing)
                + 0.3f * ValueNoise((uint)seed ^ 0xC2u, x / ClumpDetail, z / ClumpDetail);
        return Smoothstep(lo, hi, v);
    }

    /// <summary>0-1: how far inside a cluster of fragments (x, z) is, rising gradually from its fringe (a scattering of
    /// fragments) to its core (packed). Clusters are a kilometre or two across, a few hundred blocks apart where the big
    /// <see cref="Clumps"/> field is, and rare outside it.</summary>
    public static float ClusterField(ulong seed, float x, float z)
    {
        float v = 0.75f * ValueNoise((uint)seed ^ 0xC5u, x / ClusterSpacing, z / ClusterSpacing)
                + 0.25f * ValueNoise((uint)seed ^ 0xC6u, x / ClusterDetail, z / ClusterDetail);
        return Smoothstep(0.55f, 0.75f, v) * Lerp(0.15f, 1f, Clumps(seed, x, z, 0.4f, 0.6f));
    }

    /// <summary>How likely a heart is at height y, relative to the bottom of the world: on top of there being more
    /// ground low down, hearts thin out upward, so islands go from dense near the bottom to sparse near the top.</summary>
    private static float HeightDensity(float y) => Lerp(1f, 0.6f, Math.Clamp(y / HeightDensityTop, 0f, 1f));

    private const float HeightDensityTop = 1000f;

    /// <summary>0-1: 1 along narrow winding bands (where a value noise crosses its middle), fading out to either
    /// side: chains of hearts.</summary>
    public static float Chains(ulong seed, float x, float z)
    {
        float v = ValueNoise((uint)seed ^ 0xC3u, x / ChainSpacing, z / ChainSpacing);
        return 1f - Smoothstep(0f, ChainWidth, MathF.Abs(v - 0.5f));
    }

    /// <summary>Finds the middle of a cluster near (x, z): the nearest point, on a coarse grid spiralling out, well
    /// inside one.</summary>
    public static bool TryFindCluster(ulong seed, float x, float z, out float clusterX, out float clusterZ)
    {
        const float Step = 200f;
        for (int ring = 0; ring < 100; ring++)
        for (int j = -ring; j <= ring; j++)
        for (int i = -ring; i <= ring; i++)
        {
            if (System.Math.Max(System.Math.Abs(i), System.Math.Abs(j)) != ring) continue;
            clusterX = x + i * Step; clusterZ = z + j * Step;
            if (ClusterField(seed, clusterX, clusterZ) > 0.9f) return true;
        }
        clusterX = clusterZ = 0f;
        return false;
    }

    internal static float ValueNoise(uint seed, float x, float z)
    {
        float fx = MathF.Floor(x), fz = MathF.Floor(z);
        int ix = (int)fx, iz = (int)fz;
        float tx = Smoothstep(0f, 1f, x - fx), tz = Smoothstep(0f, 1f, z - fz);
        float a = Hash01(seed, ix, iz), b = Hash01(seed, ix + 1, iz);
        float c = Hash01(seed, ix, iz + 1), d = Hash01(seed, ix + 1, iz + 1);
        return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), tz);
    }

    internal static float Hash01(uint seed, int x, int z) => (HashCell(seed, 7, x, 0, z) >> 40) * (1f / (1 << 24));

    private static ulong HashCell(ulong seed, int salt, int x, int y, int z)
    {
        ulong h = seed ^ ((ulong)(uint)salt * 0xD6E8FEB86659FD93UL);
        h ^= (ulong)(uint)x * 0x9E3779B97F4A7C15UL; h = Mix(h);
        h ^= (ulong)(uint)y * 0x94D049BB133111EBUL; h = Mix(h);
        h ^= (ulong)(uint)z * 0xC2B2AE3D27D4EB4FUL; h = Mix(h);
        return h;
    }

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static int FloorDiv(float v, int size) => (int)MathF.Floor(v / size);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
