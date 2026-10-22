using System.Collections.Concurrent;
using ClearSkies.Engine.Generation;

namespace ClearSkies.Game.Generation;

/// <summary>What water there is at a block column (see <see cref="Water.At"/>).</summary>
/// <param name="Level">World Y of the top water block, or int.MinValue for none.</param>
/// <param name="Off">For a lake, how far (blocks) the column is from the middle of the lake's nearest sample cell.</param>
/// <param name="RiverDepth">For a river, how deep its bed is cut under its level; 0 for a lake.</param>
/// <param name="Bank">Beside a river, the highest its bank may stand here, or int.MaxValue.</param>
public readonly record struct WaterColumn(int Level, float Off, int RiverDepth, int Bank = int.MaxValue)
{
    public static readonly WaterColumn None = new(int.MinValue, float.MaxValue, 0);
}

/// <summary>
/// Still water, no flow: lakes and the rivers between them, worked out one piece of land at a time. A piece (the
/// ground held by one of the top row's hearts, see <see cref="HeartGrid"/>) is ringed by cracks, so whatever rains on
/// it drains off its own edges: it is a watershed of its own, worked out whole once and cached, so every chunk agrees.
///
/// Over the piece's real top (as HeartWorldGenerator wears it down), sampled every <see cref="Step"/> blocks, a
/// priority flood from its edges fills every hollow to where it would spill and gives each sample the way its water
/// goes, down to the edge. Rain (more the wetter the climate, see ContinentTerrain.Wet) gathers along those ways. A
/// hollow holds a lake if enough rain gathers into it to fill it; one left dry swallows what reaches it. Where enough
/// has gathered on its way down, it is a river, wider the more it carries, its level running down a block under
/// its ground, its bed cut a little under that.
/// </summary>
public sealed class Water
{
    public const int Step = 4;

    /// <summary>How far past its heart's cell (blocks) a piece's ground can reach: the window worked out round it.</summary>
    private const int Reach = 400;
    private const int Window = (500 + 2 * Reach) / Step;

    /// <summary>How near a piece's side face (scaled space, see HeartWorldGenerator.Solid) counts as its crack, for
    /// rivers: more than the wear and the wobble of its outline together.</summary>
    private const float FaceMargin = 44f;

    /// <summary>How far from its piece's faces a lake keeps, further than a river: the rounding of its rim as well.</summary>
    private const float LakeMargin = 64f;

    private const float MinDepth = 4f;                    // shallower hollows hold no lake
    private const int MinCells = 16, MaxCells = 1200;     // lakes of about 250 to 19,000 square blocks

    // A lake sits in open ground, not perched on a slope or by a cliff: no ground within Perch cells of it may be
    // more than PerchDrop blocks under its level (its own bed, filled to its level, doesn't count; nor do drains).
    private const int Perch = 8;
    private const float PerchDrop = 10f;

    /// <summary>Rain per sample by the climate's wetness: none at RainFrom or drier, a full share at RainFull.</summary>
    private const float RainFrom = 0.3f, RainFull = 0.85f;

    /// <summary>A hollow fills if the rain gathering into it is at least LakeNeed times its own samples.</summary>
    private const float LakeNeed = 8f;

    // A river starts where RiverFrom samples' rain has gathered, RiverNarrowest blocks wide, widening by RiverWiden
    // blocks per square root of what it carries beyond that, up to RiverWidest; and some stretches up to RiverVary wider
    // or narrower.
    private const float RiverFrom = 160f, RiverNarrowest = 3f, RiverWiden = 0.3f, RiverWidest = 10f, RiverVary = 0.35f;

    // A river winds up to Meander blocks either side of the way its water goes; its banks slope up from a block over
    // it by BankSlope blocks per block out, for BankReach blocks.
    private const float Meander = 9f, BankSlope = 0.6f, BankReach = 8f;

    /// <summary>How far (blocks) smooth noise raises or lowers the ground the rivers' ways are worked over: on gentle
    /// slopes that is enough to turn them, so they wander; steep ones still send them straight down.</summary>
    private const float Wander = 3f;

    /// <summary>How many samples from a lake its rivers' ways start to lean.</summary>
    private const int ShoreLean = 4;

    /// <summary>How many samples round a column to look for a river through it or beside it.</summary>
    private const int Search = 6;

    /// <summary>How far under its piece's top (roughly) the land may be and still be its ground, not a hole.</summary>
    private const float HoleDepth = 40f;

    /// <summary>The ground of a drain: lower than any land, so whatever reaches it spills out.</summary>
    private const float Drain = -1e6f;

    private static readonly int[] Di = { 1, -1, 0, 0, 1, 1, -1, -1 }, Dk = { 0, 0, 1, -1, 1, -1, 1, -1 };

    private static readonly ConcurrentDictionary<ulong, Water> BySeed = new();

    /// <summary>The water of the world with this seed.</summary>
    public static Water For(ulong seed) => BySeed.GetOrAdd(seed, s => new Water(s));

    private readonly ulong _seed;
    private readonly ContinentTerrain _terrain;
    private readonly ConcurrentDictionary<long, Heart> _hearts = new();
    private readonly ConcurrentDictionary<long, Piece> _pieces = new();
    private readonly FastNoiseLite _meanderX, _meanderZ, _widths, _wander;

    private Water(ulong seed)
    {
        _seed = seed;
        _terrain = ContinentTerrain.For(seed);
        _meanderX = Noise(seed + 41, 0.018f);
        _meanderZ = Noise(seed + 42, 0.018f);
        _widths = Noise(seed + 43, 0.01f);
        _wander = Noise(seed + 44, 0.008f);
    }

    private static FastNoiseLite Noise(ulong seed, float frequency)
    {
        var n = new FastNoiseLite(unchecked((int)seed));
        n.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        n.SetFractalType(FastNoiseLite.FractalType.FBm);
        n.SetFractalOctaves(2);
        n.SetFrequency(frequency);
        return n;
    }

    /// <summary>One piece's water, per sample of its window (cells from (X0, Z0), world sample coordinates).</summary>
    private sealed class Piece
    {
        public int X0, Z0;
        public int[] Lake = Array.Empty<int>();    // level of the lake over the sample, or int.MinValue
        public int[] River = Array.Empty<int>();   // level of the river through the sample, or int.MinValue
        public byte[] Width = Array.Empty<byte>(); // its width, blocks
        public int[] Down = Array.Empty<int>();    // the sample its water goes on to, or -1
        public float[] Edge = Array.Empty<float>(); // 0 at its piece's crack to 1 well inside (see EdgeFade)
        public float[] Ground = Array.Empty<float>(); // the land's real top over the sample, or Drain
    }

    /// <summary>The water at column (x, z): a lake over it or right beside it (water fills it from its ground up to
    /// the level, where the ground is under it), or a river through it.</summary>
    public WaterColumn At(int x, int z)
    {
        float px = x + 0.5f, pz = z + 0.5f;
        if (Nearest(px, pz, out int hi, out int hk) is not { Alive: true }) return WaterColumn.None;
        var piece = _pieces.GetOrAdd(Key(hi, hk), _ => Build(hi, hk));
        int ci = FloorDiv(x, Step) - piece.X0, ck = FloorDiv(z, Step) - piece.Z0;
        if (ci < 1 || ck < 1 || ci >= Window - 1 || ck >= Window - 1) return WaterColumn.None;
        int own = piece.Lake[ci + Window * ck];

        // A river: within half its width of the line from a sample's middle to the next one's down its way, that line
        // bent by noise so the river winds (less so near a lake, so it still meets it, and near its piece's crack, so
        // it doesn't wind out onto the cliff's lip). Its banks slope up from the nearest river.
        float nearLake = float.MaxValue;
        for (int dk = -4; dk <= 4; dk++)
        for (int di = -4; di <= 4; di++)
            if (piece.Lake[Math.Clamp(ci + di, 0, Window - 1) + Window * Math.Clamp(ck + dk, 0, Window - 1)] != int.MinValue)
                nearLake = MathF.Min(nearLake, MathF.Sqrt(di * di + dk * dk) * Step);
        float bend = Meander * Math.Clamp((nearLake - 2f * Step) / (2f * Step), 0f, 1f) * piece.Edge[ci + Window * ck];
        float qx = px + bend * _meanderX.GetNoise(px, pz), qz = pz + bend * _meanderZ.GetNoise(px, pz);
        int qi = FloorDiv((int)MathF.Floor(qx), Step) - piece.X0, qk = FloorDiv((int)MathF.Floor(qz), Step) - piece.Z0;
        int riverLevel = int.MinValue, width = 0, bank = int.MaxValue, riverLow = 0, bankLow = 0;
        float bestRiver = float.MaxValue, bestBank = float.MaxValue, riverX = 0f, riverZ = 0f, bankX = 0f, bankZ = 0f;
        for (int dk = -Search; dk <= Search; dk++)
        for (int di = -Search; di <= Search; di++)
        {
            int i = qi + di, k = qk + dk;
            if (i < 0 || k < 0 || i >= Window || k >= Window) continue;
            int c = i + Window * k;
            if (piece.Width[c] == 0) continue;
            float ax = (piece.X0 + i + 0.5f) * Step, az = (piece.Z0 + k + 0.5f) * Step;
            float bx = ax, bz = az;
            int d = piece.Down[c];
            if (d >= 0) { bx = (piece.X0 + d % Window + 0.5f) * Step; bz = (piece.Z0 + d / Window + 0.5f) * Step; }
            float dist = SegmentDistance(qx, qz, ax, az, bx, bz, out float t), half = piece.Width[c] * 0.5f;
            // (Its level runs down from this sample's to the next one's, or to the lake's it flows into: no higher
            // than the first, no lower than the straight line between them.)
            int to = d < 0 ? piece.River[c] : piece.River[d] != int.MinValue ? piece.River[d] : piece.Lake[d] != int.MinValue ? piece.Lake[d] : piece.River[c];
            int low = (int)MathF.Floor(piece.River[c] + t * (to - piece.River[c]));
            // (The point on the river's middle line this column is across from, back in the column's own space.)
            float mx = ax + t * (bx - ax) + px - qx, mz = az + t * (bz - az) + pz - qz;
            if (dist <= half && dist < bestRiver) { bestRiver = dist; riverLevel = piece.River[c]; riverLow = low; width = piece.Width[c]; riverX = mx; riverZ = mz; }
            else if (dist > half && dist - half < MathF.Min(BankReach, bestBank)) { bestBank = dist - half; bank = piece.River[c]; bankLow = low; bankX = mx; bankZ = mz; }
        }
        // Between those, a block under the ground on the river's middle line: so the water follows the ground's steps
        // down, neither sinking under them nor standing over them, and lies level across the river.
        float? ground = riverLevel != int.MinValue || bank != int.MaxValue ? Ground(x, z) : null;
        int Follow(int high, int low, float mx, float mz) =>
            Ground((int)MathF.Floor(mx), (int)MathF.Floor(mz)) is float g ? Math.Clamp((int)MathF.Floor(g) - 1, Math.Min(low, high), high) : high;
        if (riverLevel != int.MinValue) riverLevel = Follow(riverLevel, riverLow, riverX, riverZ);
        if (bank != int.MaxValue)
        {
            // (Steeper where the ground stands high over the river, so the bank meets it within BankReach rather than
            // leaving a wall at the end.)
            int foot = Follow(bank, bankLow, bankX, bankZ) + 1;
            float slope = ground is float g ? MathF.Max(BankSlope, (g - foot) / BankReach) : BankSlope;
            bank = foot + (int)(bestBank * slope);
        }
        int depth = width >= 8 ? 3 : width >= 4 ? 2 : 1;
        if (own != int.MinValue)
        {
            // Where a river comes in over the lake's shore, it runs on down to the lake's water (the ground round it
            // letting it down) rather than standing back behind the shore.
            if (riverLevel != int.MinValue && ground is float g && g >= own) return new(Math.Max(riverLevel, own), 0f, depth);
            return new(own, Off(ci, ck, px, pz, piece), 0);
        }
        if (riverLevel != int.MinValue) return new(riverLevel, 0f, depth);

        // Just past a lake's sampled cells the ground can still dip under its level between samples: fill those too.
        int level = int.MinValue;
        float best = float.MaxValue;
        for (int dk = -1; dk <= 1; dk++)
        for (int di = -1; di <= 1; di++)
        {
            int l = piece.Lake[ci + di + Window * (ck + dk)];
            if (l == int.MinValue) continue;
            float cx = (piece.X0 + ci + di + 0.5f) * Step - px, cz = (piece.Z0 + ck + dk + 0.5f) * Step - pz;
            float dd = cx * cx + cz * cz;
            if (dd < best) { best = dd; level = l; }
        }
        if (level == int.MinValue) return bank == int.MaxValue ? WaterColumn.None : WaterColumn.None with { Bank = bank };
        return new(level, MathF.Sqrt(best), 0);
    }

    /// <summary>The land's top at column (x, z), from its piece's samples round it (or, by a drain, as its piece's
    /// top, see HeartWorldGenerator.PieceTop), or null where no piece holds it.</summary>
    public float? Ground(int x, int z)
    {
        float px = x + 0.5f, pz = z + 0.5f;
        if (Nearest(px, pz, out int hi, out int hk) is not { Alive: true } h) return null;
        var piece = _pieces.GetOrAdd(Key(hi, hk), _ => Build(hi, hk));
        float fi = px / Step - 0.5f - piece.X0, fk = pz / Step - 0.5f - piece.Z0;
        int i = (int)MathF.Floor(fi), k = (int)MathF.Floor(fk);
        if (i >= 0 && k >= 0 && i < Window - 1 && k < Window - 1)
        {
            float a = piece.Ground[i + Window * k], b = piece.Ground[i + 1 + Window * k];
            float c = piece.Ground[i + Window * (k + 1)], d = piece.Ground[i + 1 + Window * (k + 1)];
            if (a != Drain && b != Drain && c != Drain && d != Drain)
            {
                float tx = fi - i, tz = fk - k;
                return (a + (b - a) * tx) * (1f - tz) + (c + (d - c) * tx) * tz;
            }
        }
        return HeartWorldGenerator.PieceTop(_terrain.Height(px, pz), h.Surface, h.Drop);
    }

    private static float Off(int ci, int ck, float px, float pz, Piece piece)
    {
        float cx = (piece.X0 + ci + 0.5f) * Step - px, cz = (piece.Z0 + ck + 0.5f) * Step - pz;
        return MathF.Sqrt(cx * cx + cz * cz);
    }

    private static float SegmentDistance(float px, float pz, float ax, float az, float bx, float bz, out float t)
    {
        float vx = bx - ax, vz = bz - az, len2 = vx * vx + vz * vz;
        t = len2 > 0f ? Math.Clamp(((px - ax) * vx + (pz - az) * vz) / len2, 0f, 1f) : 0f;
        float dx = px - (ax + t * vx), dz = pz - (az + t * vz);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Works out the water of the piece of the top row's heart in cell (hi, hk).</summary>
    private Piece Build(int hi, int hk)
    {
        const int N = Window * Window;
        float size = HeartGrid.Layers[0].CellSize;
        var piece = new Piece { X0 = FloorDiv(hi * (int)size - Reach, Step), Z0 = FloorDiv(hk * (int)size - Reach, Step) };
        var self = TopHeart(hi, hk);
        var hearts = new List<Heart>();
        for (int k = hk - 2; k <= hk + 2; k++)
        for (int i = hi - 2; i <= hi + 2; i++)
            hearts.Add(TopHeart(i, k));

        // The piece's ground, and how far each sample is from its faces; elsewhere drains.
        var ground = new float[N];
        var face = new float[N];
        var rain = new float[N];
        var lean = new float[N];
        for (int k = 0; k < Window; k++)
        for (int i = 0; i < Window; i++)
        {
            int c = i + Window * k;
            float x = (piece.X0 + i + 0.5f) * Step, z = (piece.Z0 + k + 0.5f) * Step;
            face[c] = FaceDistance(hearts, self, x, z);
            if (i == 0 || k == 0 || i == Window - 1 || k == Window - 1 || face[c] < FaceMargin) { ground[c] = Drain; continue; }
            ground[c] = 0f; // (worked out below)
            rain[c] = Smoothstep(RainFrom, RainFull, _terrain.Wet(x, z));
            lean[c] = Wander * _wander.GetNoise(x, z) * EdgeFade(face[c]);
        }

        // The ground is the land's real top (as HeartWorldGenerator makes it, rounded off by its faces and all), a
        // chunk column at a time so the generator loads the hearts round each once. Where it is far under the piece's
        // top, a hole into the caves under it, it is a drain too.
        var generator = new HeartWorldGenerator(_seed);
        int cx0 = FloorDiv(piece.X0 * Step, 32), cz0 = FloorDiv(piece.Z0 * Step, 32);
        int cx1 = FloorDiv((piece.X0 + Window) * Step, 32), cz1 = FloorDiv((piece.Z0 + Window) * Step, 32);
        for (int cz = cz0; cz <= cz1; cz++)
        for (int cx = cx0; cx <= cx1; cx++)
        for (int k = Math.Max(0, CeilDiv(cz * 32 - Step / 2, Step) - piece.Z0); k < Window && (piece.Z0 + k) * Step + Step / 2 < (cz + 1) * 32; k++)
        for (int i = Math.Max(0, CeilDiv(cx * 32 - Step / 2, Step) - piece.X0); i < Window && (piece.X0 + i) * Step + Step / 2 < (cx + 1) * 32; i++)
        {
            int c = i + Window * k;
            if (ground[c] == Drain) continue;
            int x = (piece.X0 + i) * Step + Step / 2, z = (piece.Z0 + k) * Step + Step / 2;
            float roughly = HeartWorldGenerator.PieceTop(_terrain.Height(x, z), self.Surface, self.Drop);
            ground[c] = generator.Top(x, z) is int top && top > roughly - HoleDepth ? top + 0.5f : Drain;
        }
        piece.Ground = ground;

        // Priority flood from the drains inwards, lowest first: each sample filled to at least the level it was
        // reached at, so each hollow fills to its lowest way out, and its water goes back the way it was reached.
        var filled = new float[N];
        var down = new int[N];
        var order = new int[N];
        int count = Flood(ground, filled, down, order, c => ground[c] == Drain ? Drain : float.NaN);

        // The hollows: samples filled over their ground, joined side by side.
        var hollow = new int[N];
        Array.Fill(hollow, -1);
        var cellsOf = new List<List<int>>();
        var deepest = new List<float>();
        var stack = new Stack<int>();
        for (int s = 0; s < N; s++)
        {
            if (hollow[s] >= 0 || ground[s] == Drain || filled[s] - ground[s] < 0.01f) continue;
            int id = cellsOf.Count;
            var cells = new List<int>();
            float deep = 0f;
            hollow[s] = id; stack.Push(s);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                cells.Add(c);
                deep = MathF.Max(deep, filled[c] - ground[c]);
                int ci = c % Window, ck = c / Window;
                for (int n = 0; n < 4; n++)
                {
                    int nc = ci + Di[n] + Window * (ck + Dk[n]);
                    if (hollow[nc] >= 0 || ground[nc] == Drain || filled[nc] - ground[nc] < 0.01f) continue;
                    hollow[nc] = id; stack.Push(nc);
                }
            }
            cellsOf.Add(cells);
            deepest.Add(deep);
        }

        // Rain gathers down the ways, from the furthest upstream first, through every hollow: how much reaches each.
        var gathered = new float[N];
        var inflow = new float[cellsOf.Count];
        Array.Copy(rain, gathered, N);
        for (int o = count - 1; o >= 0; o--)
        {
            int c = order[o], d = down[c], h = hollow[c];
            if (h >= 0 && (d < 0 || hollow[d] != h)) inflow[h] += gathered[c];
            if (d >= 0) gathered[d] += gathered[c];
        }

        // Each deep enough hollow fills as far as the rain reaching it can keep it: to a block under its spill point,
        // overflowing, if that covers no more than inflow / LakeNeed samples, otherwise to the level that does, keeping
        // what reaches it. None perched; one too small to be a pond is left dry.
        piece.Lake = new int[N];
        Array.Fill(piece.Lake, int.MinValue);
        var level = new int[cellsOf.Count];
        var full = new bool[cellsOf.Count];
        var sink = new bool[cellsOf.Count]; // deep enough to keep what reaches it, full or not
        for (int h = 0; h < cellsOf.Count; h++)
        {
            level[h] = int.MinValue;
            var cells = cellsOf[h];
            if (deepest[h] < MinDepth) continue;
            sink[h] = true;
            int spill = (int)MathF.Floor(filled[cells[0]]) - 1;
            // (Near its piece's faces it stays dry: no higher than the lowest ground there.)
            int l = spill;
            foreach (int c in cells)
                if (face[c] < LakeMargin) l = Math.Min(l, (int)MathF.Floor(ground[c]));
            var beds = new List<float>();
            foreach (int c in cells)
                if (ground[c] < l) beds.Add(ground[c]);
            if (beds.Count < MinCells) continue;
            beds.Sort();
            int target = Math.Min((int)(inflow[h] / LakeNeed), MaxCells);
            if (target < beds.Count)
            {
                if (target < MinCells) continue;
                l = Math.Min(l, (int)MathF.Floor(beds[target]));
            }
            if (Perched(cells, filled, l)) continue;
            int wet = 0;
            foreach (int c in cells) if (ground[c] < l) wet++;
            if (wet < MinCells) continue;
            level[h] = l;
            full[h] = l == spill;
            foreach (int c in cells)
                if (ground[c] < l) piece.Lake[c] = l;
        }

        // The ways again, now ending in the lakes that keep what reaches them and in the deep hollows left dry; an
        // overflowing lake's water level, flat, passes on what reaches it out over its spill point. These ways lean
        // with the noise, so rivers wander over gentle ground, but not by a lake, so they don't wander along its shore.
        for (int k = 0; k < Window; k++)
        for (int i = 0; i < Window; i++)
        {
            int c = i + Window * k;
            if (lean[c] == 0f) continue;
            float near = float.MaxValue;
            for (int dk = -ShoreLean; dk <= ShoreLean; dk++)
            for (int di = -ShoreLean; di <= ShoreLean; di++)
            {
                int ni = i + di, nk = k + dk;
                if (ni >= 0 && nk >= 0 && ni < Window && nk < Window && piece.Lake[ni + Window * nk] != int.MinValue)
                    near = MathF.Min(near, MathF.Sqrt(di * di + dk * dk));
            }
            lean[c] *= Math.Clamp((near - 1f) / (ShoreLean - 1f), 0f, 1f);
        }
        var surface = new float[N];
        for (int c = 0; c < N; c++)
        {
            int h = hollow[c];
            surface[c] = h >= 0 && full[h] && piece.Lake[c] != int.MinValue ? level[h] : ground[c];
        }
        count = Flood(surface, filled, down, order, c =>
        {
            if (ground[c] == Drain) return Drain;
            int h = hollow[c];
            if (h < 0 || !sink[h] || full[h]) return float.NaN;
            return level[h] != int.MinValue ? (piece.Lake[c] != int.MinValue ? level[h] : float.NaN) : ground[c];
        }, lean);
        Array.Copy(rain, gathered, N);
        for (int o = count - 1; o >= 0; o--)
        {
            int c = order[o], d = down[c];
            if (d >= 0 && !Ends(d)) gathered[d] += gathered[c];
        }
        bool Ends(int c)
        {
            int h = hollow[c];
            return ground[c] == Drain || (h >= 0 && sink[h] && !full[h] && (level[h] == int.MinValue || piece.Lake[c] != int.MinValue));
        }

        // Rivers: where enough has gathered. Each one's level is a block under its ground, never rising on its way
        // (worked from upstream down), and not under a lake it meets.
        piece.River = new int[N];
        piece.Width = new byte[N];
        piece.Down = down;
        piece.Edge = new float[N];
        for (int c = 0; c < N; c++) piece.Edge[c] = EdgeFade(face[c]);
        Array.Fill(piece.River, int.MaxValue);
        for (int o = count - 1; o >= 0; o--)
        {
            int c = order[o];
            if (gathered[c] < RiverFrom || piece.Lake[c] != int.MinValue || Ends(c)) { piece.River[c] = int.MinValue; continue; }
            int ci = c % Window, ck = c / Window;
            int lakeBeside = int.MinValue;
            for (int k = ck - 1; k <= ck + 1; k++)
            for (int i = ci - 1; i <= ci + 1; i++)
                lakeBeside = Math.Max(lakeBeside, piece.Lake[i + Window * k]);
            piece.River[c] = Math.Max(Math.Min(piece.River[c], (int)MathF.Floor(ground[c]) - 1), lakeBeside);
            float vary = 1f + RiverVary * _widths.GetNoise((piece.X0 + ci) * Step, (piece.Z0 + ck) * Step);
            piece.Width[c] = (byte)Math.Clamp(MathF.Round((RiverNarrowest + RiverWiden * MathF.Sqrt(gathered[c] - RiverFrom)) * vary), 2f, RiverWidest);
            int d = down[c];
            if (d >= 0 && piece.River[d] != int.MinValue) piece.River[d] = Math.Min(piece.River[d], piece.River[c]);
        }
        for (int c = 0; c < N; c++)
            if (piece.River[c] == int.MaxValue) piece.River[c] = int.MinValue;
        return piece;
    }

    /// <summary>Priority flood over <paramref name="ground"/>: from the sources (where <paramref name="source"/> gives a
    /// level rather than NaN) inwards, lowest first, each sample filled to at least the level it was reached at; its
    /// water goes back the way it was reached (<paramref name="down"/>, -1 for none). Returns how many samples it
    /// reached, in <paramref name="order"/>, sources and drains aside. With <paramref name="lean"/>, samples are taken
    /// in the order of their level plus their lean, which bends the ways (and so the fill is only roughly right).</summary>
    private static int Flood(float[] ground, float[] filled, int[] down, int[] order, Func<int, float> source, float[]? lean = null)
    {
        const int N = Window * Window;
        var done = new bool[N];
        var queue = new PriorityQueue<int, float>();
        var isSource = new bool[N];
        for (int c = 0; c < N; c++)
        {
            down[c] = -1;
            float s = source(c);
            if (float.IsNaN(s)) continue;
            filled[c] = s; done[c] = true; isSource[c] = true;
            queue.Enqueue(c, s);
        }
        int count = 0;
        while (queue.TryDequeue(out int c, out _))
        {
            float level = filled[c];
            if (!isSource[c]) order[count++] = c;
            int ci = c % Window, ck = c / Window;
            // (Eight ways round, so rivers can run on the diagonals too.)
            for (int n = 0; n < 8; n++)
            {
                int ni = ci + Di[n], nk = ck + Dk[n];
                if (ni < 0 || nk < 0 || ni >= Window || nk >= Window) continue;
                int nc = ni + Window * nk;
                if (done[nc]) continue;
                done[nc] = true;
                filled[nc] = MathF.Max(ground[nc], level);
                down[nc] = ground[c] == Drain ? -1 : c;
                queue.Enqueue(nc, filled[nc] + (lean?[nc] ?? 0f));
            }
        }
        return count;
    }

    /// <summary>Whether a lake at <paramref name="level"/> over these samples would be perched: ground within Perch
    /// samples of it more than PerchDrop under it (drains aside).</summary>
    private static bool Perched(List<int> cells, float[] filled, int level)
    {
        foreach (int c in cells)
        {
            int ci = c % Window, ck = c / Window;
            for (int k = Math.Max(0, ck - Perch); k <= Math.Min(Window - 1, ck + Perch); k++)
            for (int i = Math.Max(0, ci - Perch); i <= Math.Min(Window - 1, ci + Perch); i++)
            {
                float f = filled[i + Window * k];
                if (f != Drain && f < level - PerchDrop) return true;
            }
        }
        return false;
    }

    /// <summary>How far (x, z) is inside the piece of heart <paramref name="self"/> (scaled space, see
    /// HeartWorldGenerator.Solid): less than 0 if it is another's, or 0 if that piece is gone.</summary>
    private static float FaceDistance(List<Heart> hearts, Heart self, float x, float z)
    {
        if (!self.Alive) return 0f;
        float by = self.Depth * HeartGrid.VerticalScale;
        float bestD = (self.X - x) * (self.X - x) + by * by + (self.Z - z) * (self.Z - z);
        float least = float.MaxValue;
        foreach (var h in hearts)
        {
            if (!h.Exists || h == self) continue;
            float hy = h.Depth * HeartGrid.VerticalScale;
            float d = (h.X - x) * (h.X - x) + hy * hy + (h.Z - z) * (h.Z - z);
            float ex = h.X - self.X, ey = hy - by, ez = h.Z - self.Z;
            least = MathF.Min(least, (d - bestD) / (2f * MathF.Sqrt(ex * ex + ey * ey + ez * ez)));
        }
        return least;
    }

    /// <summary>The top row's heart nearest (x, z) at the surface (counting depth VerticalScale times over), and its
    /// cell.</summary>
    private Heart? Nearest(float x, float z, out int hi, out int hk)
    {
        float size = HeartGrid.Layers[0].CellSize;
        int i0 = (int)MathF.Floor(x / size), k0 = (int)MathF.Floor(z / size);
        Heart? best = null;
        float bestD = float.MaxValue;
        hi = hk = 0;
        for (int k = k0 - 1; k <= k0 + 1; k++)
        for (int i = i0 - 1; i <= i0 + 1; i++)
        {
            var h = TopHeart(i, k);
            if (!h.Exists) continue;
            float hy = h.Depth * HeartGrid.VerticalScale;
            float d = (h.X - x) * (h.X - x) + hy * hy + (h.Z - z) * (h.Z - z);
            if (d < bestD) { bestD = d; best = h; hi = i; hk = k; }
        }
        return best;
    }

    private Heart TopHeart(int i, int k) => _hearts.GetOrAdd(Key(i, k), _ => HeartGrid.At(_seed, 0, i, -1, k));

    private static long Key(int i, int k) => ((long)i << 32) ^ (uint)k;

    /// <summary>How freely a river may wander at this far from its piece's faces: not at all where they count as its
    /// crack, fully once as far in as a lake keeps.</summary>
    private static float EdgeFade(float face) => Math.Clamp((face - FaceMargin) / (LakeMargin - FaceMargin), 0f, 1f);

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;

    private static int CeilDiv(int a, int b) => -FloorDiv(-a, b);
}
