using System.Collections.Concurrent;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Lakes: still water filling the hollows the terrain already has, no flow and no carving. The world is cut into
/// <see cref="Tile"/>-block tiles; each works out its lakes once from the terrain's height over itself and a margin
/// round it, sampled every <see cref="Step"/> blocks, by filling every hollow up to the point where it would spill
/// over (a priority flood). The ground it fills is the land's real top: each piece's top as HeartWorldGenerator wears
/// it down (flattened and dropped from the terrain surface). The cracks between pieces and the gaps where a piece is
/// gone are drains, so no lake sits over a crack, hangs over a gap or rests against a piece's worn edge. A tile keeps
/// only lakes lying wholly inside it, so each lake is decided by exactly one tile and every chunk agrees. Its level is
/// a block under the spill point, so the water stands below its banks.
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

    /// <summary>The ground of a drain: lower than any land, so whatever reaches it spills out.</summary>
    private const float Drain = -1e6f;

    private static readonly ConcurrentDictionary<ulong, Lakes> BySeed = new();

    /// <summary>The lakes of the world with this seed.</summary>
    public static Lakes For(ulong seed) => BySeed.GetOrAdd(seed, s => new Lakes(s));

    private readonly ulong _seed;
    private readonly ContinentTerrain _terrain;
    private readonly ConcurrentDictionary<long, int[]> _tiles = new();

    private Lakes(ulong seed)
    {
        _seed = seed;
        _terrain = ContinentTerrain.For(seed);
    }

    /// <summary>The level (world Y of the top water block) of the lake at or right beside column (x, z), or
    /// int.MinValue for none. Water fills the column from its ground up to the level, where the ground is under it.</summary>
    public int Level(int x, int z)
    {
        int gx = FloorDiv(x, Step), gz = FloorDiv(z, Step);
        int own = CellLevel(gx, gz);
        if (own != int.MinValue) return own;
        // Just past a lake's sampled cells the ground can still dip under its level between samples: fill those too.
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int l = CellLevel(gx + dx, gz + dz);
            if (l != int.MinValue) return l;
        }
        return int.MinValue;
    }

    private int CellLevel(int gx, int gz)
    {
        int tx = FloorDiv(gx, Core), tz = FloorDiv(gz, Core);
        var levels = _tiles.GetOrAdd(((long)tx << 32) ^ (uint)tz, _ => Build(tx, tz));
        return levels[(gx - tx * Core) + Core * (gz - tz * Core)];
    }

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
        // lies wholly in the core.
        var levels = new int[Core * Core];
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
                if (ci <= Off || ck <= Off || ci >= Off + Core - 1 || ck >= Off + Core - 1) inside = false;
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
            if (!inside || deepest < MinDepth || cells.Count < MinCells || cells.Count > MaxCells) continue;
            int lakeLevel = (int)MathF.Floor(filled[start]) - 1;
            foreach (int c in cells) levels[(c % Window - Off) + Core * (c / Window - Off)] = lakeLevel;
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

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
