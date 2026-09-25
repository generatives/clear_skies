using System.Collections.Concurrent;

namespace ClearSkies.Game.Generation;

/// <summary>The size classes of hearts, largest first; each has its own grid (see <see cref="HeartGrid"/>).</summary>
public enum HeartClass { Large, Medium, Small }

/// <summary>How a heart's support is shaped, and so the island it holds.</summary>
public enum SupportShape
{
    /// <summary>A rounded lens: soft edges, a bowl underneath.</summary>
    Lens,
    /// <summary>A faceted outline with vertical walls down from the terrain, then a cone underneath: big cliff faces
    /// that show the terrain's rock layers.</summary>
    Cliff,
    /// <summary>A narrower cliff shape with a long spike underneath.</summary>
    Spire,
}

/// <summary>
/// One island heart and the support around it: the part of the <see cref="ContinentTerrain"/> inside the support is the
/// island the heart holds up. Positions are world blocks. For each column the support is one height span
/// (<see cref="Span"/>), from <see cref="Bottom"/> up to <see cref="Y"/> + <see cref="Up"/>; the island's top is the
/// terrain surface wherever that is lower.
/// </summary>
public struct Heart
{
    public HeartClass Class;
    public SupportShape Shape;
    public float X, Y, Z;
    public float Radius;
    public float Up;          // support height above the heart
    public float Wall;        // lens: underside depth below the heart; cliff/spire: height of the walls below it
    public float Spike;       // cliff/spire: depth of the cone below the walls
    public float SpikePower;  // cliff/spire: the cone's profile ((1 - t)^power: under 1 bulges, over 1 is pointed)
    public float Rotation;    // cliff/spire: the outline's rotation
    public int Sides;         // cliff/spire: the outline's corners
    public uint Id;           // per-heart noise (outline corners, lens edge wobble)
    public bool Buried;       // deep under the terrain surface: a bare-rock island with no terrain top

    /// <summary>The farthest the support reaches from the heart, horizontally.</summary>
    public readonly float Reach => Radius * 1.2f;

    public readonly float YMin => Shape == SupportShape.Lens ? Y - Wall - 2f : Y - Wall - Spike - 2f;
    public readonly float YMax => Y + Up + 1f;

    /// <summary>The support's span at column (x, z), if the column is inside it.</summary>
    public readonly bool Span(float x, float z, out float bottom, out float top)
    {
        float dx = x - X, dz = z - Z;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        bottom = top = 0f;
        if (d >= Reach) return false;

        if (Shape == SupportShape.Lens)
        {
            // Wobbly round outline; underside a bowl, (1 - t²)^0.75, rounded at the rim; top a dome.
            float wobble = HeartGrid.ValueNoise(Id, x / (Radius * 0.5f), z / (Radius * 0.5f));
            float t = d / (Radius * (0.8f + 0.4f * wobble));
            if (t >= 1f) return false;
            float u = 1f - t * t;
            bottom = Y - Wall * MathF.Pow(u, 0.75f);
            top = Y + Up * MathF.Sqrt(u);
            return true;
        }

        // A polygon of Sides corners at radii between 0.7 and 1.15 of the radius: straight faces with corners. The walls
        // go straight down from the terrain (the top is flat, cutting a mesa where the terrain rises past it), then a
        // cone of Spike below them.
        float angle = MathF.Atan2(dz, dx) - Rotation;
        float sector = MathF.Tau / Sides;
        float a = (angle % MathF.Tau + MathF.Tau) % MathF.Tau / sector;
        int i = (int)a;
        float phi = (a - i) * sector;
        float ri = Corner(i % Sides), rj = Corner((i + 1) % Sides);
        float edge = ri * rj * MathF.Sin(sector) / (ri * MathF.Sin(phi) + rj * MathF.Sin(sector - phi));
        edge *= 0.97f + 0.06f * HeartGrid.ValueNoise(Id ^ 0x5EEDu, x / 10f, z / 10f); // rough, not glassy, faces
        float tc = d / edge;
        if (tc >= 1f) return false;
        bottom = Y - Wall - Spike * MathF.Pow(1f - tc, SpikePower);
        top = Y + Up;
        return true;
    }

    // Corners at 0.7-1.15 of the radius: with the faces' 3% roughness, still inside Reach.
    private readonly float Corner(int i) => Radius * (0.7f + 0.45f * HeartGrid.Hash01(Id, i, 0));
}

/// <summary>
/// Where island hearts are. The world is a <see cref="ContinentTerrain"/> with hearts scattered through it; each heart
/// holds up the terrain inside its support, and nothing else exists. Hearts sit on one grid of cells per
/// <see cref="HeartClass"/>. A cell's first slot is a heart just under the terrain surface (so its island has a real top:
/// grass, sand, snow); its further slots are hearts deeper down, now and then, holding bare-rock islands underneath.
/// As the terrain is mostly low, islands are dense near the bottom of the world and rare near the top, where only the
/// peaks are.
///
/// Where hearts are is coherent rather than even. Large hearts are plateaus (<see cref="Plateau"/>): 2-4 km across but
/// only a few hundred blocks deep, mostly cliff-sided, and only inside clusters (the <see cref="Clumps"/> field), packed
/// so tightly that their supports overlap and merge into big broken landmasses, with chasms where they don't quite
/// meet, and open sky between clusters. Medium and small hearts are outlying islands around the clusters, and strung
/// along narrow winding bands of the <see cref="Chains"/> field. A support may reach past its own cell (up to the class's
/// greatest reach), so a column's hearts are found in the cells within that reach of it.
/// </summary>
public static class HeartGrid
{
    /// <summary>One class's grid: cells of <see cref="CellSize"/> blocks, each with <see cref="Slots"/> slots (a
    /// surface heart, then deeper ones), a heart's radius, and the chance of a surface heart outside clusters and inside
    /// them (and, but for the large class, on chains). A deeper slot holds a heart with <see cref="DeepChance"/> of
    /// that.</summary>
    private readonly record struct ClassDef(int CellSize, int Slots, float MinRadius, float MaxRadius,
                                            float ChanceLow, float ChanceHigh, float DeepChance);

    private static readonly ClassDef[] Classes =
    {
        //   cell  slots  radius        chance: outside, inside clusters; deep
        new(2600, 1,     1100f, 1800f, 0f,    0.90f,  0f),    // Large: plateaus, clusters only
        new(1100, 3,      180f,  450f, 0.01f, 0.50f,  0.30f), // Medium: around clusters, on chains
        new( 448, 2,       40f,  110f, 0.003f,0.18f,  0.20f), // Small
    };

    /// <summary>A plateau's support, in blocks rather than by its radius, so a wide one stays flat: height above the
    /// heart, and below it a lens's depth or a cliff's walls and cone.</summary>
    private const float PlateauUpMin = 250f, PlateauUpMax = 450f, PlateauLensMin = 250f, PlateauLensMax = 500f,
                        PlateauWallMin = 120f, PlateauWallMax = 300f, PlateauSpikeMin = 150f, PlateauSpikeMax = 400f;

    public const int ClassCount = 3;

    /// <summary>The lowest an island may reach: above the cloud sea (see SkySettings.CloudSeaAltitude).</summary>
    private const float LowestBottom = IslandGrid.WorldBottom + 56f;

    // Clumps: value noise at these spacings (blocks); chains: where another value noise crosses its middle, within
    // ChainWidth of it.
    private const float ClumpSpacing = 9000f, ClumpDetail = 3000f, ChainSpacing = 4000f, ChainWidth = 0.04f;

    public static int Layers(HeartClass c) => Classes[(int)c].Slots;

    /// <summary>Writes the hearts of class <paramref name="c"/> whose cells are within the class's greatest reach of
    /// the box (every slot) into <paramref name="output"/> starting at <paramref name="count"/>, and returns the new
    /// count: every heart whose support can touch the box, and some that can't.</summary>
    public static int Collect(ulong seed, HeartClass c, float minX, float minZ, float maxX, float maxZ,
                              Span<Heart> output, int count)
    {
        var def = Classes[(int)c];
        float pad = def.MaxRadius * 1.2f;
        int x0 = FloorDiv(minX - pad, def.CellSize), x1 = FloorDiv(maxX + pad, def.CellSize);
        int z0 = FloorDiv(minZ - pad, def.CellSize), z1 = FloorDiv(maxZ + pad, def.CellSize);
        int layers = Layers(c);
        for (int cz = z0; cz <= z1; cz++)
        for (int cx = x0; cx <= x1; cx++)
        for (int cy = 0; cy < layers; cy++)
        {
            if (count == output.Length) return count;
            if (Placed(seed, c, cx, cy, cz, out output[count])) count++;
        }
        return count;
    }

    /// <summary>All three classes' hearts in the cells overlapping the box.</summary>
    public static int CollectAll(ulong seed, float minX, float minZ, float maxX, float maxZ, Span<Heart> output)
    {
        int count = 0;
        for (int c = 0; c < ClassCount; c++) count = Collect(seed, (HeartClass)c, minX, minZ, maxX, maxZ, output, count);
        return count;
    }

    public static bool Placed(ulong seed, HeartClass c, int cx, int cy, int cz, out Heart heart)
    {
        var key = (seed, (int)c, cx, cy, cz);
        if (!Cache.TryGetValue(key, out var v))
        {
            v = (TryPlace(seed, c, cx, cy, cz, out var h), h);
            if (Cache.Count > CacheLimit) Cache.Clear();
            Cache[key] = v;
        }
        heart = v.Item2;
        return v.Item1;
    }

    private const int CacheLimit = 1 << 21;
    private static readonly ConcurrentDictionary<(ulong, int, int, int, int), (bool, Heart)> Cache = new();

    private static bool TryPlace(ulong seed, HeartClass c, int cx, int cy, int cz, out Heart heart)
    {
        heart = default;
        var def = Classes[(int)c];
        uint id = (uint)HashCell(seed, 100 + (int)c, cx, cy, cz);
        var rng = new SplitMix64Rng(HashCell(seed, 100 + (int)c, cx, cy, cz));
        float roll = rng.NextFloat01();
        if (roll >= def.ChanceHigh) return false; // cheap reject

        float radius = Lerp(def.MinRadius, def.MaxRadius, MathF.Pow(rng.NextFloat01(), 1.5f));
        float x = (cx + rng.NextFloat01()) * def.CellSize;
        float z = (cz + rng.NextFloat01()) * def.CellSize;
        bool plateau = c == HeartClass.Large;
        // Plateaus fill the clusters; the smaller hearts gather over a wider area around them, and on chains.
        float bias = plateau ? Clumps(seed, x, z) : MathF.Max(Clumps(seed, x, z, 0.42f, 0.6f), 0.8f * Chains(seed, x, z));
        float chance = Lerp(def.ChanceLow, def.ChanceHigh, bias) * (cy == 0 ? 1f : def.DeepChance);
        if (roll >= chance) return false;

        float shapeRoll = rng.NextFloat01();
        var shape = plateau ? (shapeRoll < 0.35f ? SupportShape.Lens : SupportShape.Cliff)
                  : shapeRoll < 0.45f ? SupportShape.Lens : shapeRoll < 0.85f ? SupportShape.Cliff : SupportShape.Spire;
        heart = new Heart
        {
            Class = c, Shape = shape, X = x, Z = z, Id = id,
            Radius = shape == SupportShape.Spire ? radius * 0.6f : radius,
            Rotation = rng.NextFloat01() * MathF.Tau,
            Sides = 5 + (int)(rng.NextFloat01() * 5f),
        };
        if (plateau) Plateau(ref heart, ref rng);
        else switch (shape)
        {
            case SupportShape.Lens:
                heart.Up = radius * rng.NextRange(0.5f, 0.9f);
                heart.Wall = radius * rng.NextRange(0.35f, 0.6f);
                break;
            case SupportShape.Cliff:
                heart.Up = radius * rng.NextRange(0.6f, 1.0f);
                heart.Wall = radius * rng.NextRange(0.25f, 0.55f);
                heart.Spike = radius * rng.NextRange(0.3f, 0.8f);
                heart.SpikePower = rng.NextRange(0.6f, 1.6f);
                break;
            default:
                heart.Up = radius * rng.NextRange(0.5f, 0.8f);
                heart.Wall = radius * rng.NextRange(0.15f, 0.35f);
                heart.Spike = radius * rng.NextRange(1.0f, 1.8f);
                heart.SpikePower = rng.NextRange(0.7f, 1.2f);
                break;
        }

        // Slot 0: just under the terrain surface, so the island has its top. Deeper slots: far enough down that the
        // support stops short of the surface, a bare-rock island under the one above (or merged into it).
        float surface = ContinentTerrain.For(seed).Height(x, z);
        if (cy == 0)
            heart.Y = surface - heart.Up * rng.NextRange(0.15f, 0.8f);
        else
        {
            heart.Y = surface - heart.Up - radius * rng.NextRange(0.4f, 1.2f) * cy;
            heart.Buried = true;
        }

        // Too deep for the world: trim the cone, then the walls, rather than lose the island.
        float over = LowestBottom - heart.YMin;
        if (over > 0f && shape != SupportShape.Lens)
        {
            float trim = MathF.Min(over, heart.Spike);
            heart.Spike -= trim; over -= trim;
            heart.Wall -= MathF.Min(over, MathF.Max(heart.Wall - 60f, 0f));
        }
        return heart.YMin >= LowestBottom; // (the top is clipped to the world's)
    }

    /// <summary>A plateau's support (the large class): flat for its width, sized in blocks.</summary>
    private static void Plateau(ref Heart heart, ref SplitMix64Rng rng)
    {
        heart.Up = rng.NextRange(PlateauUpMin, PlateauUpMax);
        if (heart.Shape == SupportShape.Lens)
            heart.Wall = rng.NextRange(PlateauLensMin, PlateauLensMax);
        else
        {
            heart.Wall = rng.NextRange(PlateauWallMin, PlateauWallMax);
            heart.Spike = rng.NextRange(PlateauSpikeMin, PlateauSpikeMax);
            heart.SpikePower = rng.NextRange(0.6f, 1.6f);
        }
    }

    /// <summary>0-1: how far inside a cluster (x, z) is: 0 below <paramref name="lo"/> of the cluster field, 1 above
    /// <paramref name="hi"/> (the defaults are the plateaus' clusters; a lower range reaches around them).</summary>
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

    /// <summary>Finds a surface heart of the large class near (x, z): the nearest to it within a few cells.</summary>
    public static bool TryFindLarge(ulong seed, float x, float z, out Heart nearest)
    {
        nearest = default;
        float best = float.MaxValue;
        int size = Classes[0].CellSize, cx0 = FloorDiv(x, size), cz0 = FloorDiv(z, size);
        for (int cz = cz0 - 12; cz <= cz0 + 12; cz++)
        for (int cx = cx0 - 12; cx <= cx0 + 12; cx++)
        for (int cy = 0; cy < Layers(HeartClass.Large); cy++)
        {
            if (!Placed(seed, HeartClass.Large, cx, cy, cz, out var h) || h.Buried) continue;
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
