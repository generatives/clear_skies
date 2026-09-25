using System.Collections.Concurrent;

namespace ClearSkies.Game.Generation;

/// <summary>The kinds of hearts, each with its own grid (see <see cref="HeartGrid"/>). Surface hearts sit just under the
/// terrain surface and hold up everything above them, so their islands' tops are the terrain (hills, mountains, snow
/// peaks). Buried hearts are scattered through the ground in 3D and each holds up a slab: a bounded height with its own
/// noisy, mostly flat top, so a mountain or the deep ground becomes a stack of islands.</summary>
public enum HeartKind { SurfaceLarge, SurfaceMedium, SurfaceSmall, BuriedLarge, BuriedMedium, BuriedSmall }

/// <summary>How a heart's support is shaped, and so the island it holds.</summary>
public enum SupportShape
{
    /// <summary>A rounded lens: soft edges, a bowl underneath.</summary>
    Lens,
    /// <summary>A faceted outline, some of whose sides are vertical walls (big cliff faces that show the terrain's rock
    /// layers) and the rest rounded slopes, with a cone underneath.</summary>
    Cliff,
    /// <summary>A narrower cliff shape with a long spike underneath.</summary>
    Spire,
}

/// <summary>
/// One island heart and the support around it: the part of the <see cref="ContinentTerrain"/> inside the support is the
/// island the heart holds up. Positions are world blocks. For each column the support is one height span
/// (<see cref="Span"/>): a surface heart's goes up to the top of the world (the terrain decides the island's top), a buried
/// heart's to its own rolling top <see cref="Up"/> above it.
/// </summary>
public struct Heart
{
    public HeartKind Kind;
    public SupportShape Shape;
    public float X, Y, Z;
    public float Radius;
    public float Up;          // buried: height of the slab's top above the heart
    public float Roll;        // buried: how far the top rolls up and down
    public float Wall;        // lens: underside depth below the heart; cliff/spire: depth of the walls below it
    public float Spike;       // cliff/spire: depth of the cone below the walls
    public float SpikePower;  // cliff/spire: the cone's profile ((1 - t)^power: under 1 bulges, over 1 is pointed)
    public float Rotation;    // cliff/spire: the outline's rotation
    public int Sides;         // cliff/spire: the outline's corners
    public uint Id;           // per-heart noise (outline corners, which sides are cliffs, lens edge wobble, the top)

    public readonly bool Surface => Kind <= HeartKind.SurfaceSmall;

    /// <summary>The farthest the support reaches from the heart, horizontally.</summary>
    public readonly float Reach => Radius * 1.2f;

    /// <summary>The support's lowest point, but for a surface heart's root (see <see cref="Root"/>).</summary>
    public readonly float YMin => Shape == SupportShape.Lens ? Y - Wall - 2f : Y - Wall - Spike - 2f;
    public readonly float YMax => Surface ? IslandGrid.WorldTop : Y + Up + Roll + 1f;

    /// <summary>How much deeper a surface heart's underside hangs per block the ground stands above the heart: high
    /// ground has a root below it, as mountains do, so an island with a mountain on it isn't a thin slab carrying it.</summary>
    public const float RootFactor = 0.6f;

    /// <summary>A surface heart's root under ground standing at <paramref name="groundLevel"/>.</summary>
    public readonly float Root(float groundLevel) => Surface ? RootFactor * MathF.Max(0f, groundLevel - Y) : 0f;

    /// <summary>The support's span at column (x, z), if the column is inside it, given the terrain's broad height there
    /// (<paramref name="groundLevel"/>: averaged over a hundred blocks or so, so a root is a broad bulge under high
    /// ground rather than every peak mirrored).</summary>
    public readonly bool Span(float x, float z, float groundLevel, out float bottom, out float top)
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
            t = d / (Radius * (0.8f + 0.4f * wobble));
            if (t >= 1f) return false;
            cliff = 0f;
            depth = Wall * MathF.Pow(1f - t * t, 0.75f);
        }
        else
        {
            // A polygon of Sides corners at radii between 0.7 and 1.15 of the radius: straight faces with corners.
            // Along a cliff side the support goes straight down (Wall below the heart, then a cone of Spike); along the
            // others its depth rounds off towards the edge like a lens, blending over the last part of a side into the
            // next.
            float angle = MathF.Atan2(dz, dx) - Rotation;
            float sector = MathF.Tau / Sides;
            float a = (angle % MathF.Tau + MathF.Tau) % MathF.Tau / sector;
            int i = (int)a;
            float phi = (a - i) * sector;
            float ri = Corner(i % Sides), rj = Corner((i + 1) % Sides);
            float edge = ri * rj * MathF.Sin(sector) / (ri * MathF.Sin(phi) + rj * MathF.Sin(sector - phi));
            edge *= 0.97f + 0.06f * HeartGrid.ValueNoise(Id ^ 0x5EEDu, x / 10f, z / 10f); // rough, not glassy, faces
            t = d / edge;
            if (t >= 1f) return false;
            float f = phi / sector;
            cliff = IsCliff(i);
            if (f < 0.15f) cliff = Lerp(0.5f * (IsCliff(i - 1) + cliff), cliff, f / 0.15f);
            else if (f > 0.85f) cliff = Lerp(cliff, 0.5f * (cliff + IsCliff(i + 1)), (f - 0.85f) / 0.15f);
            float round = MathF.Pow(1f - t * t, 0.75f);
            depth = (Wall + Spike * MathF.Pow(1f - t, SpikePower)) * Lerp(round, 1f, cliff);
        }

        // A root barely thins towards the edge (only right at the rim), so high ground near the edge still stands on
        // a thick base.
        float root = Root(groundLevel);
        if (root > 0f) depth += root * Lerp(MathF.Pow(1f - t * t, 0.2f), 1f, cliff);
        bottom = Y - depth;

        if (Surface) { top = IslandGrid.WorldTop; return true; }

        // A buried heart's slab: a broad roll plus finer bumps, dropping off towards a rounded edge (not a cliff one).
        float broad = HeartGrid.ValueNoise(Id ^ 0x70Fu, x / (Radius * 0.35f), z / (Radius * 0.35f));
        float fine = HeartGrid.ValueNoise(Id ^ 0x70Eu, x / 24f, z / 24f);
        float drop = Up * 0.6f * Smoothstep(0.7f, 1f, t) * (1f - cliff);
        top = Y + Up + Roll * (1.4f * broad + 0.6f * fine - 1f) - drop;
        return top > bottom + 1f;
    }

    // Corners at 0.7-1.15 of the radius: with the faces' 3% roughness, still inside Reach.
    private readonly float Corner(int i) => Radius * (0.7f + 0.45f * HeartGrid.Hash01(Id, i, 0));

    /// <summary>1 if side i (from corner i to the next) is a cliff, 0 if it slopes: most spires' sides are cliffs,
    /// about 60% of a cliff's.</summary>
    private readonly float IsCliff(int i)
        => HeartGrid.Hash01(Id, ((i % Sides) + Sides) % Sides, 1) < (Shape == SupportShape.Spire ? 0.85f : 0.6f) ? 1f : 0f;

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
/// cells, at most one heart per cell: surface kinds on 2D cells, a heart just under the terrain surface; buried kinds on
/// 3D cells, a heart anywhere in the ground (cells above the terrain have none). Buried hearts are as likely anywhere in
/// the ground, and there is far more ground low down than up in the peaks, so islands are dense near the bottom of the
/// world and rare near the top.
///
/// Where hearts are is coherent rather than even: they gather in clusters (the <see cref="Clumps"/> field) with open sky
/// between, and the smaller ones also along narrow winding bands (the <see cref="Chains"/> field). A support may reach
/// past its own cell (up to its kind's greatest reach), so a column's hearts are found in the cells within that reach.
/// </summary>
public static class HeartGrid
{
    /// <summary>One kind's grid: cells of <see cref="CellSize"/> blocks across and (buried kinds)
    /// <see cref="CellHeight"/> tall, a heart's radius, and the chance of a heart outside clusters and inside them.</summary>
    private readonly record struct KindDef(int CellSize, int CellHeight, float MinRadius, float MaxRadius,
                                           float ChanceLow, float ChanceHigh);

    private static readonly KindDef[] Kinds =
    {
        //   cell height  radius        chance: outside, inside clusters
        new(2600,    0,  700f, 1300f, 0f,     0.45f), // SurfaceLarge: clusters only
        new(1100,    0,  150f,  420f, 0.02f,  0.40f), // SurfaceMedium
        new( 448,    0,   40f,  110f, 0.005f, 0.14f), // SurfaceSmall
        new(1600,  500,  450f,  850f, 0.02f,  0.60f), // BuriedLarge: wide, flat slabs
        new( 700,  300,  120f,  330f, 0.04f,  0.70f), // BuriedMedium
        new( 320,  200,   35f,  100f, 0.01f,  0.30f), // BuriedSmall
    };

    public const int KindCount = 6;

    /// <summary>How far under the terrain surface a surface heart sits (below the lowest ground around it).</summary>
    private const float HeartDepthMin = 10f, HeartDepthMax = 60f;

    /// <summary>How far under the terrain a buried heart's slab top must stay, at its middle.</summary>
    private const float BuriedCover = 20f;

    /// <summary>The lowest an island may reach: above the cloud sea (see SkySettings.CloudSeaAltitude).</summary>
    internal const float LowestBottom = IslandGrid.WorldBottom + 56f;

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
        int y0 = 0, y1 = 0;
        if (def.CellHeight > 0)
        {
            y0 = FloorDiv(LowestBottom, def.CellHeight);
            y1 = FloorDiv(IslandGrid.WorldTop, def.CellHeight);
        }
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

        float radius = Lerp(def.MinRadius, def.MaxRadius, MathF.Pow(rng.NextFloat01(), 1.5f));
        float x = (cx + rng.NextFloat01()) * def.CellSize;
        float z = (cz + rng.NextFloat01()) * def.CellSize;
        bool large = k is HeartKind.SurfaceLarge or HeartKind.BuriedLarge;
        // The large hearts fill the clusters; the smaller ones gather over a wider area around them, and on chains.
        float bias = large ? Clumps(seed, x, z) : MathF.Max(Clumps(seed, x, z, 0.42f, 0.6f), 0.8f * Chains(seed, x, z));
        if (roll >= Lerp(def.ChanceLow, def.ChanceHigh, bias)) return false;

        float shapeRoll = rng.NextFloat01();
        var shape = large ? (shapeRoll < 0.4f ? SupportShape.Lens : SupportShape.Cliff)
                  : shapeRoll < 0.55f ? SupportShape.Lens : shapeRoll < 0.9f ? SupportShape.Cliff : SupportShape.Spire;
        if (shape == SupportShape.Spire) radius *= 0.6f;
        heart = new Heart
        {
            Kind = k, Shape = shape, X = x, Z = z, Id = (uint)hash, Radius = radius,
            Rotation = rng.NextFloat01() * MathF.Tau,
            Sides = 5 + (int)(rng.NextFloat01() * 5f),
        };

        // Underside: a lens's depth, or a cliff's walls and cone. Large ones are sized in blocks rather than by their
        // radius, so a wide one stays flat; buried ones are flatter than surface ones.
        float flat = def.CellHeight > 0 ? 0.6f : 1f;
        switch (shape)
        {
            case SupportShape.Lens:
                heart.Wall = flat * (large ? rng.NextRange(150f, 320f) : radius * rng.NextRange(0.35f, 0.6f));
                break;
            case SupportShape.Cliff:
                heart.Wall = flat * (large ? rng.NextRange(80f, 200f) : radius * rng.NextRange(0.2f, 0.4f));
                heart.Spike = flat * (large ? rng.NextRange(80f, 250f) : radius * rng.NextRange(0.3f, 0.7f));
                heart.SpikePower = rng.NextRange(0.6f, 1.6f);
                break;
            default:
                heart.Wall = radius * rng.NextRange(0.15f, 0.3f);
                heart.Spike = radius * rng.NextRange(0.8f, 1.3f);
                heart.SpikePower = rng.NextRange(0.7f, 1.2f);
                break;
        }

        var terrain = ContinentTerrain.For(seed);
        float surface = terrain.Height(x, z);
        if (def.CellHeight == 0)
        {
            // Just under the lowest ground around it, so its island has the terrain for a top all across and high
            // ground in it stands on a root.
            float ground = surface;
            for (int i = 0; i < 6; i++)
            {
                float a = i * (MathF.Tau / 6f);
                ground = MathF.Min(ground, terrain.Height(x + 0.5f * radius * MathF.Cos(a), z + 0.5f * radius * MathF.Sin(a)));
            }
            heart.Y = ground - rng.NextRange(HeartDepthMin, HeartDepthMax);
        }
        else
        {
            // Anywhere in its cell, as long as its slab is under the ground there.
            heart.Y = (cy + rng.NextFloat01()) * def.CellHeight;
            heart.Up = large ? rng.NextRange(30f, 80f) : rng.NextRange(15f, 40f) + radius * 0.1f;
            heart.Roll = large ? rng.NextRange(15f, 40f) : rng.NextRange(5f, 12f) + radius * 0.06f;
            if (heart.Y + heart.Up + heart.Roll > surface - BuriedCover) return false;
        }

        // Too deep for the world: trim the cone, then the walls, rather than lose the island.
        float over = LowestBottom - heart.YMin;
        if (over > 0f && shape != SupportShape.Lens)
        {
            float trim = MathF.Min(over, heart.Spike);
            heart.Spike -= trim; over -= trim;
            heart.Wall -= MathF.Min(over, MathF.Max(heart.Wall - 40f, 0f));
        }
        return heart.YMin >= LowestBottom;
    }

    /// <summary>0-1: how far inside a cluster (x, z) is: 0 below <paramref name="lo"/> of the cluster field, 1 above
    /// <paramref name="hi"/> (the defaults are the large hearts' clusters; a lower range reaches around them).</summary>
    public static float Clumps(ulong seed, float x, float z, float lo = 0.52f, float hi = 0.68f)
    {
        float v = 0.7f * ValueNoise((uint)seed ^ 0xC1u, x / ClumpSpacing, z / ClumpSpacing)
                + 0.3f * ValueNoise((uint)seed ^ 0xC2u, x / ClumpDetail, z / ClumpDetail);
        return Smoothstep(lo, hi, v);
    }

    /// <summary>0-1: 1 along narrow winding bands (where a value noise crosses its middle), fading out to either
    /// side: chains of hearts.</summary>
    public static float Chains(ulong seed, float x, float z)
    {
        float v = ValueNoise((uint)seed ^ 0xC3u, x / ChainSpacing, z / ChainSpacing);
        return 1f - Smoothstep(0f, ChainWidth, MathF.Abs(v - 0.5f));
    }

    /// <summary>Finds a large surface heart near (x, z): the nearest to it within a few cells.</summary>
    public static bool TryFindLarge(ulong seed, float x, float z, out Heart nearest)
    {
        nearest = default;
        float best = float.MaxValue;
        int size = Kinds[(int)HeartKind.SurfaceLarge].CellSize, cx0 = FloorDiv(x, size), cz0 = FloorDiv(z, size);
        for (int cz = cz0 - 12; cz <= cz0 + 12; cz++)
        for (int cx = cx0 - 12; cx <= cx0 + 12; cx++)
        {
            if (!Placed(seed, HeartKind.SurfaceLarge, cx, 0, cz, out var h)) continue;
            float d = (h.X - x) * (h.X - x) + (h.Z - z) * (h.Z - z);
            if (d < best) { best = d; nearest = h; }
        }
        return best < float.MaxValue;
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
