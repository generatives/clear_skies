using System.Collections.Concurrent;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Lakes: still water filling the hollows the terrain already has, no flow and no carving. The world is cut into
/// <see cref="Tile"/>-block tiles; each works out its lakes once from the terrain's height over itself and a margin
/// round it, sampled every <see cref="Step"/> blocks, by filling every hollow up to the point where it would spill
/// over (a priority flood). The ground it fills is the land's real top: each piece's top as HeartWorldGenerator wears
/// it down (flattened and dropped from the terrain surface). The cracks between pieces and the gaps where a piece is
/// gone are drains, so no lake sits over a crack, hangs over a gap or rests against a piece's worn edge. Each lake is
/// decided by exactly one tile, the one holding its first cell, and may reach into its neighbours (not too near the
/// edge of its window), so every chunk agrees. Its level is a block under the spill point, so the water stands below
/// its banks. None lies in a desert, and the drier the climate the fewer hollows hold one.
/// </summary>
public sealed class Lakes
{
    public const int Tile = 512, Margin = 96, Step = 4;

    private const int Core = Tile / Step, Window = (Tile + 2 * Margin) / Step, Off = Margin / Step;
    private const float MinDepth = 2.5f;                  // shallower hollows are left dry
    private const int MinCells = 10, MaxCells = 2500;     // lakes of about 160 to 40,000 square blocks

    /// <summary>How near a piece's side face (scaled space, see HeartWorldGenerator.Solid) counts as its crack: more
    /// than the wear, the rounding of its rim and the wobble of its outline together.</summary>
    private const float FaceMargin = 64f;

    // A lake sits in open ground, not perched on a slope or by a cliff: no ground within Perch cells of it may be
    // more than PerchDrop blocks under its level (its own bed, filled to its level, doesn't count; nor do drains,
    // already FaceMargin away).
    private const int Perch = 8;
    private const float PerchDrop = 10f;

    /// <summary>A hollow holds a lake by the climate's wetness (see ContinentTerrain.Wet): never at WetFrom or drier,
    /// always at WetFull or wetter.</summary>
    private const float WetFrom = 0.3f, WetFull = 0.85f;

    /// <summary>The ground of a drain: lower than any land, so whatever reaches it spills out.</summary>
    private const float Drain = -1e6f;

    private static readonly ConcurrentDictionary<ulong, Lakes> BySeed = new();

    /// <summary>The lakes of the world with this seed.</summary>
    public static Lakes For(ulong seed) => BySeed.GetOrAdd(seed, s => new Lakes(s));

    private readonly ulong _seed;
    private readonly ContinentTerrain _terrain;
    private readonly ConcurrentDictionary<long, int[]> _tiles = new(), _owned = new();

    private Lakes(ulong seed)
    {
        _seed = seed;
        _terrain = ContinentTerrain.For(seed);
    }

    /// <summary>The level (world Y of the top water block) of the lake at or right beside column (x, z), or
    /// int.MinValue for none. Water fills the column from its ground up to the level, where the ground is under it.</summary>
    public int Level(int x, int z) => Level(x, z, out _);

    /// <summary>As <see cref="Level(int, int)"/>, with how far (blocks) column (x, z) is from the middle of the
    /// lake's nearest sample cell.</summary>
    public int Level(int x, int z, out float off)
    {
        int gx = FloorDiv(x, Step), gz = FloorDiv(z, Step);
        // Just past a lake's sampled cells the ground can still dip under its level between samples: fill those too.
        int level = int.MinValue;
        float best = float.MaxValue;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int l = CellLevel(gx + dx, gz + dz);
            if (l == int.MinValue) continue;
            float cx = (gx + dx + 0.5f) * Step - (x + 0.5f), cz = (gz + dz + 0.5f) * Step - (z + 0.5f);
            float d = cx * cx + cz * cz;
            if (d < best) { best = d; level = l; }
        }
        off = MathF.Sqrt(best);
        return level;
    }

    private int CellLevel(int gx, int gz)
    {
        int tx = FloorDiv(gx, Core), tz = FloorDiv(gz, Core);
        var levels = _tiles.GetOrAdd(Key(tx, tz), _ => Gather(tx, tz));
        return levels[(gx - tx * Core) + Core * (gz - tz * Core)];
    }

    /// <summary>Tile (tx, tz)'s lake level per sample cell of its core, int.MinValue where it is dry: from the lakes
    /// it and its neighbours own.</summary>
    private int[] Gather(int tx, int tz)
    {
        var levels = new int[Core * Core];
        Array.Fill(levels, int.MinValue);
        for (int nz = tz - 1; nz <= tz + 1; nz++)
        for (int nx = tx - 1; nx <= tx + 1; nx++)
        {
            var owned = _owned.GetOrAdd(Key(nx, nz), _ => Build(nx, nz));
            int di = Off + (tx - nx) * Core, dk = Off + (tz - nz) * Core;
            for (int k = 0; k < Core; k++)
            {
                int wk = k + dk;
                if (wk < 0 || wk >= Window) continue;
                for (int i = 0; i < Core; i++)
                {
                    int wi = i + di;
                    if (wi < 0 || wi >= Window || owned[wi + Window * wk] == int.MinValue) continue;
                    levels[i + Core * k] = owned[wi + Window * wk];
                }
            }
        }
        return levels;
    }

    private static long Key(int tx, int tz) => ((long)tx << 32) ^ (uint)tz;

    /// <summary>Tile (tx, tz)'s lake level per sample cell of its core, int.MinValue where it is dry.</summary>
    private int[] Build(int tx, int tz)
    {
        float x0 = tx * Tile - Margin + Step * 0.5f, z0 = tz * Tile - Margin + Step * 0.5f;
        var hearts = TopHearts(tx, tz);
        var height = new float[Window * Window];
        for (int k = 0; k < Window; k++)
        for (int i = 0; i < Window; i++)
            height[i + Window * k] = Ground(hearts, x0 + i * Step, z0 + k * Step);

        // Priority flood: from the window's edge and the drains inwards, lowest first, each cell filled to at least the
        // level it was reached at, so each hollow fills to its lowest way out.
        var filled = new float[Window * Window];
        var done = new bool[Window * Window];
        var queue = new PriorityQueue<int, float>();
        for (int k = 0; k < Window; k++)
        for (int i = 0; i < Window; i++)
            if (i == 0 || k == 0 || i == Window - 1 || k == Window - 1 || height[i + Window * k] == Drain)
            {
                int c = i + Window * k;
                filled[c] = height[c]; done[c] = true; queue.Enqueue(c, height[c]);
            }
        while (queue.TryDequeue(out int c, out float level))
        {
            int ci = c % Window, ck = c / Window;
            for (int n = 0; n < 4; n++)
            {
                int ni = ci + (n == 0 ? 1 : n == 1 ? -1 : 0), nk = ck + (n == 2 ? 1 : n == 3 ? -1 : 0);
                if (ni < 0 || nk < 0 || ni >= Window || nk >= Window) continue;
                int nc = ni + Window * nk;
                if (done[nc]) continue;
                done[nc] = true;
                filled[nc] = MathF.Max(height[nc], level);
                queue.Enqueue(nc, filled[nc]);
            }
        }

        // Each hollow (cells filled over their ground, joined side by side) is a lake if it is deep and big enough and
        // is this tile's: its first cell (lowest row, then column: the same whichever window finds it) in the core, and
        // all of it far enough inside the window that the flood saw its surroundings.
        var levels = new int[Window * Window];
        Array.Fill(levels, int.MinValue);
        var seen = new bool[Window * Window];
        var cells = new List<int>();
        var stack = new Stack<int>();
        for (int start = 0; start < Window * Window; start++)
        {
            if (seen[start] || filled[start] - height[start] < 0.01f) continue;
            cells.Clear();
            stack.Push(start); seen[start] = true;
            bool inside = true;
            float deepest = 0f;
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                cells.Add(c);
                int ci = c % Window, ck = c / Window;
                if (ci <= Perch || ck <= Perch || ci >= Window - 1 - Perch || ck >= Window - 1 - Perch) inside = false;
                deepest = MathF.Max(deepest, filled[c] - height[c]);
                for (int n = 0; n < 4; n++)
                {
                    int ni = ci + (n == 0 ? 1 : n == 1 ? -1 : 0), nk = ck + (n == 2 ? 1 : n == 3 ? -1 : 0);
                    if (ni < 0 || nk < 0 || ni >= Window || nk >= Window) continue;
                    int nc = ni + Window * nk;
                    if (seen[nc] || filled[nc] - height[nc] < 0.01f) continue;
                    seen[nc] = true; stack.Push(nc);
                }
            }
            int si = start % Window, sk = start / Window;
            if (si < Off || sk < Off || si >= Off + Core || sk >= Off + Core) continue;
            if (!inside || deepest < MinDepth || cells.Count < MinCells || cells.Count > MaxCells) continue;
            int lakeLevel0 = (int)MathF.Floor(filled[start]) - 1;
            bool perched = false;
            foreach (int c in cells)
            {
                int ci = c % Window, ck = c / Window;
                for (int k = ck - Perch; k <= ck + Perch && !perched; k++)
                for (int i = ci - Perch; i <= ci + Perch; i++)
                {
                    float f = filled[i + Window * k];
                    if (f != Drain && f < lakeLevel0 - PerchDrop) { perched = true; break; }
                }
                if (perched) break;
            }
            if (perched) continue;
            bool dry = false;
            foreach (int c in cells)
                if (Dry(x0 + (c % Window) * Step, z0 + (c / Window) * Step, height[c])) { dry = true; break; }
            if (dry) continue;
            // The wetter the climate, the more hollows hold a lake: decided by the lake's first cell, so the same in
            // every window.
            float sx = x0 + si * Step, sz = z0 + sk * Step;
            float chance = Smoothstep(WetFrom, WetFull, _terrain.Wet(sx, sz));
            int gsx = tx * Core - Off + si, gsz = tz * Core - Off + sk;
            if (Trees.Hash(gsx, 0, gsz, _seed ^ 0x1A4E) % 1000 >= chance * 1000f) continue;
            int lakeLevel = (int)MathF.Floor(filled[start]) - 1;
            // Only cells actually under the water are kept, so the shore round them (see Level) is just by the water,
            // not over the flats round it at its level.
            foreach (int c in cells)
                if (height[c] < lakeLevel) levels[c] = lakeLevel;
        }
        return levels;
    }

    /// <summary>The top row's hearts round tile (tx, tz) (see <see cref="HeartGrid.At"/>).</summary>
    private List<Heart> TopHearts(int tx, int tz)
    {
        float size = HeartGrid.Layers[0].CellSize;
        int i0 = (int)MathF.Floor((tx * Tile - Margin) / size) - 1, i1 = (int)MathF.Floor(((tx + 1) * Tile + Margin) / size) + 1;
        int k0 = (int)MathF.Floor((tz * Tile - Margin) / size) - 1, k1 = (int)MathF.Floor(((tz + 1) * Tile + Margin) / size) + 1;
        var hearts = new List<Heart>();
        for (int k = k0; k <= k1; k++)
        for (int i = i0; i <= i1; i++)
            hearts.Add(HeartGrid.At(_seed, 0, i, -1, k));
        return hearts;
    }

    /// <summary>Whether ground at (x, z) and <paramref name="top"/> high is desert, badlands or a desert's edge: no
    /// lakes there.</summary>
    private bool Dry(float x, float z, float top)
    {
        float heat = Biomes.Heat(top, _terrain.Heat(x, z)), wet = _terrain.Wet(x, z);
        var biome = Biomes.Pick(heat, wet);
        return biome is Biome.Desert or Biome.Badlands || Biomes.Dry(heat, wet, Biomes.SandBlend) > 0.5f;
    }

    /// <summary>The land's real top at (x, z): its top piece's top (see HeartWorldGenerator.Solid), or
    /// <see cref="Drain"/> where that piece is gone or (x, z) is in or near the crack round it.</summary>
    private float Ground(List<Heart> hearts, float x, float z)
    {
        // At the surface a block's nearest heart is one of the top row's, counting depth VerticalScale times over.
        int best = -1;
        float bestD = float.MaxValue;
        for (int n = 0; n < hearts.Count; n++)
        {
            var h = hearts[n];
            if (!h.Exists) continue;
            float hy = h.Depth * HeartGrid.VerticalScale;
            float d = (h.X - x) * (h.X - x) + hy * hy + (h.Z - z) * (h.Z - z);
            if (d < bestD) { bestD = d; best = n; }
        }
        if (best < 0 || !hearts[best].Alive) return Drain;
        var b = hearts[best];
        for (int n = 0; n < hearts.Count; n++)
        {
            var h = hearts[n];
            if (n == best || !h.Exists) continue;
            float hy = h.Depth * HeartGrid.VerticalScale, by = b.Depth * HeartGrid.VerticalScale;
            float d = (h.X - x) * (h.X - x) + hy * hy + (h.Z - z) * (h.Z - z);
            float ex = h.X - b.X, ey = hy - by, ez = h.Z - b.Z;
            if ((d - bestD) / (2f * MathF.Sqrt(ex * ex + ey * ey + ez * ez)) < FaceMargin) return Drain;
        }
        float surface = _terrain.Height(x, z);
        return surface - MathF.Max(0f, surface - b.Surface) * (1f - HeartWorldGenerator.Flatten) - HeartWorldGenerator.MaxDrop * b.Drop;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
