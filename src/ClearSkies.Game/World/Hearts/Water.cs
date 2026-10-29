using System.Collections.Concurrent;
using ClearSkies.Engine.Core;
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
/// The lakes and how much water runs where come from <see cref="Hydrology"/>, which settles the water over the whole
/// landmass; this lays them over the piece's real top (as HeartWorldGenerator wears it down), sampled every
/// <see cref="Step"/> blocks. A lake fills the samples under its level; where enough runs it is a river, wider the more
/// it carries, its level running down a block under its ground, its bed cut a little under that.
/// </summary>
public sealed class Water
{
    public const int Step = 4;

    /// <summary>How far past its heart's cell (blocks) a piece's ground can reach: the window worked out round it.</summary>
    private static int Reach => (int)(0.8f * HeartGrid.Layers[0].CellSize);
    private static int Window => ((int)HeartGrid.Layers[0].CellSize + 2 * Reach) / Step;

    /// <summary>How near a piece's side face (scaled space, see HeartWorldGenerator.Solid) counts as its crack, for
    /// rivers: more than the wear and the wobble of its outline together.</summary>
    private static float FaceMargin => 44f + 24f * (HeartWorldGenerator.CrackWidth - 1f);

    /// <summary>How far from its piece's faces a lake keeps, further than a river: the rounding of its rim as well.</summary>
    private static float LakeMargin => FaceMargin + 20f;

    // A river starts where RiverFrom columns' rain runs (see Hydrology), RiverNarrowest blocks wide, widening by RiverWiden
    // blocks per square root of what it carries beyond that, up to RiverWidest; and some stretches up to RiverVary wider
    // or narrower.
    private const float RiverFrom = 40f, RiverNarrowest = 2f, RiverWiden = 0.25f, RiverWidest = 10f, RiverVary = 0.35f;

    // A river winds up to Meander blocks either side of the way its water goes; its banks slope up from a block over
    // it by BankSlope blocks per block out, or steeper to meet the ground within its reach: BankNear blocks (a cut) to
    // BankFar (a gentle slope), varying along the river by noise.
    private const float Meander = 9f, BankSlope = 0.6f, BankNear = 6f, BankFar = 18f;

    /// <summary>How far from a lake (blocks) a river widens, up to MouthWiden times wider, and wears down to it.</summary>
    private const float MouthReach = 16f, MouthWiden = 1f;

    /// <summary>How many samples round a column to look for a river through it or beside it.</summary>
    private const int Search = 10; // (as far as the widest channel Erosion wears reaches)

    /// <summary>How far under its piece's top (roughly) the land may be and still be its ground, not a hole.</summary>
    private const float HoleDepth = 40f;

    /// <summary>The ground of a drain: lower than any land, so whatever reaches it spills out.</summary>
    private const float Drain = -1e6f;

    private static readonly ConcurrentDictionary<ulong, Water> BySeed = new();

    /// <summary>The water of the world with this seed.</summary>
    public static Water For(ulong seed) => BySeed.GetOrAdd(seed, s => new Water(s));

    private readonly ulong _seed;
    private readonly ContinentTerrain _terrain;
    private readonly ConcurrentDictionary<long, Heart> _hearts = new();
    private readonly ConcurrentDictionary<long, Piece> _pieces = new();
    private readonly FastNoiseLite _meanderX, _meanderZ, _widths, _banks;

    private Water(ulong seed)
    {
        _seed = seed;
        _terrain = ContinentTerrain.For(seed);
        _meanderX = Noise(seed + 41, 0.018f);
        _meanderZ = Noise(seed + 42, 0.018f);
        _widths = Noise(seed + 43, 0.01f);
        _banks = Noise(seed + 45, 0.006f);
        _settle = new WaterSettle(this, seed);
    }

    private readonly WaterSettle _settle;

    /// <summary>The water at column (x, z) once it has settled on the blocks (see <see cref="WaterSettle"/>): its level,
    /// or int.MinValue for none; and the top of the ground there once a river has cut it, or int.MaxValue.</summary>
    public (int Level, int Ground) Settled(int x, int z) => _settle.At(x, z);

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
        var piece = PieceOf(hi, hk);
        int ci = FloorDiv(x, Step) - piece.X0, ck = FloorDiv(z, Step) - piece.Z0;
        if (ci < 1 || ck < 1 || ci >= Window - 1 || ck >= Window - 1) return WaterColumn.None;
        int own = piece.Lake[ci + Window * ck];

        // A river: within half its width of the line from a sample's middle to the next one's down its way, that line
        // bent by noise so the river winds (less so near a lake, so it still meets it, and near its piece's crack, so
        // it doesn't wind out onto the cliff's lip). Its banks slope up from the nearest river.
        float nearLake = float.MaxValue;
        int lakeLevel = int.MinValue;
        for (int dk = -4; dk <= 4; dk++)
        for (int di = -4; di <= 4; di++)
        {
            int l = piece.Lake[Math.Clamp(ci + di, 0, Window - 1) + Window * Math.Clamp(ck + dk, 0, Window - 1)];
            float dd = MathF.Sqrt(di * di + dk * dk) * Step;
            if (l != int.MinValue && dd < nearLake) { nearLake = dd; lakeLevel = l; }
        }
        // (Coming into a lake, a river widens and wears its way down to the lake's level, cutting a bay into the
        // shore rather than tumbling over it.)
        float mouth = Math.Clamp(1f - nearLake / MouthReach, 0f, 1f);
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
            float dist = SegmentDistance(qx, qz, ax, az, bx, bz, out float t), half = piece.Width[c] * 0.5f * (1f + MouthWiden * mouth);
            // (Its level runs down from this sample's to the next one's, or to the lake's it flows into: no higher
            // than the first, no lower than the straight line between them.)
            int to = d < 0 ? piece.River[c] : piece.River[d] != int.MinValue ? piece.River[d] : piece.Lake[d] != int.MinValue ? piece.Lake[d] : piece.River[c];
            int low = (int)MathF.Floor(piece.River[c] + t * (to - piece.River[c]));
            // (The point on the river's middle line this column is across from, back in the column's own space.)
            float mx = ax + t * (bx - ax) + px - qx, mz = az + t * (bz - az) + pz - qz;
            if (dist <= half && dist < bestRiver) { bestRiver = dist; riverLevel = piece.River[c]; riverLow = low; width = piece.Width[c]; riverX = mx; riverZ = mz; }
            else if (dist > half && dist - half < MathF.Min(BankFar, bestBank)) { bestBank = dist - half; bank = piece.River[c]; bankLow = low; bankX = mx; bankZ = mz; }
        }
        // Between those, a block under the ground on the river's middle line: so the water follows the ground's steps
        // down, neither sinking under them nor standing over them, and lies level across the river.
        float? ground = riverLevel != int.MinValue || bank != int.MaxValue ? Ground(x, z) : null;
        int Follow(int high, int low, float mx, float mz)
        {
            int l = Ground((int)MathF.Floor(mx), (int)MathF.Floor(mz)) is float g ? Math.Clamp((int)MathF.Floor(g) - 1, Math.Min(low, high), high) : high;
            return mouth > 0f && lakeLevel < l ? (int)MathF.Round(l + (lakeLevel - l) * mouth) : l;
        }
        if (riverLevel != int.MinValue) riverLevel = Follow(riverLevel, riverLow, riverX, riverZ);
        float reach = BankNear + (BankFar - BankNear) * Smoothstep(-0.3f, 0.3f, _banks.GetNoise(px, pz));
        if (bank != int.MaxValue && bestBank >= reach) bank = int.MaxValue;
        if (bank != int.MaxValue)
        {
            // (Steeper where the ground stands high over the river, so the bank meets it within its reach rather than
            // leaving a wall at the end.)
            int foot = Follow(bank, bankLow, bankX, bankZ) + 1;
            float slope = ground is float g ? MathF.Max(BankSlope, (g - foot) / reach) : BankSlope;
            bank = foot + (int)(bestBank * slope);
        }
        int depth = width >= 8 ? 3 : width >= 4 ? 2 : 1;
        // (Shallower toward a lake, so its bed runs out onto the lake's rather than ending at a ledge under the water.)
        depth = Math.Max(1, (int)MathF.Round(depth * (1f - mouth)));
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
        var piece = PieceOf(hi, hk);
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
        return RoughTop(px, pz, h);
    }

    /// <summary>Roughly the top of heart <paramref name="h"/>'s piece at (x, z): the generator's, before its edges are
    /// rounded and water cuts it.</summary>
    internal float RoughTop(float x, float z, Heart h)
    {
        float height = _terrain.Height(x, z, out float broad);
        return HeartWorldGenerator.PieceTop(height, h.Surface, h.Drop) + Erosion.For(_seed).Of(x, z).Shift(x, z, height - broad);
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

    // A piece's working array, a window's samples (on the large object heap, where dropping it costs a full
    // collection): rented for each piece worked out, and cleared. Enough kept for a few worked out at once.
    private static readonly FixedArrayPool<float> Floats = new(Window * Window, maxKept: 8);

    private static T[] Rent<T>(FixedArrayPool<T> pool)
    {
        var a = pool.Rent();
        Array.Clear(a);
        return a;
    }

    // Each worker's generator for the pieces' ground.
    [ThreadStatic] private static HeartWorldGenerator? t_generator;

    /// <summary>Works out the water of the piece of the top row's heart in cell (hi, hk).</summary>
    private Piece Build(int hi, int hk)
    {
        int N = Window * Window;
        float size = HeartGrid.Layers[0].CellSize;
        var piece = new Piece { X0 = FloorDiv(hi * (int)size - Reach, Step), Z0 = FloorDiv(hk * (int)size - Reach, Step) };
        var self = TopHeart(hi, hk);
        var hearts = new List<Heart>();
        for (int k = hk - 2; k <= hk + 2; k++)
        for (int i = hi - 2; i <= hi + 2; i++)
            hearts.Add(TopHeart(i, k));

        // The piece's ground, and how far each sample is from its faces; elsewhere drains.
        var ground = new float[N];
        var face = Rent(Floats);
        for (int k = 0; k < Window; k++)
        for (int i = 0; i < Window; i++)
        {
            int c = i + Window * k;
            float x = (piece.X0 + i + 0.5f) * Step, z = (piece.Z0 + k + 0.5f) * Step;
            face[c] = FaceDistance(hearts, self, x, z);
            if (i == 0 || k == 0 || i == Window - 1 || k == Window - 1 || face[c] < FaceMargin) { ground[c] = Drain; continue; }
            ground[c] = 0f; // (worked out below)
        }

        // The ground is the land's real top (as HeartWorldGenerator makes it, rounded off by its faces and all), a
        // chunk column at a time so the generator loads the hearts round each once. Where it is far under the piece's
        // top, a hole into the caves under it, it is a drain too.
        var generator = t_generator is { } kept && kept.Seed == _seed ? kept : t_generator = new HeartWorldGenerator(_seed);
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
            float roughly = RoughTop(x, z, self);
            ground[c] = generator.Top(x, z) is int top && top > roughly - HoleDepth ? top + 0.5f : Drain;
        }
        piece.Ground = ground;

        // The lakes and the water running over the land, as Hydrology settles them a cell at a time over its whole
        // landmass: a lake fills each sample of a lake's cell its ground is under; a river runs where enough runs over
        // a cell, through one sample per cell (the one over the cell's middle), on to the cell its water goes to.
        var hydrology = Hydrology.For(_seed).Of(self.X, self.Z);
        piece.Lake = new int[N];
        piece.River = new int[N];
        piece.Width = new byte[N];
        piece.Down = new int[N];
        piece.Edge = new float[N];
        Array.Fill(piece.Lake, int.MinValue);
        Array.Fill(piece.River, int.MinValue);
        Array.Fill(piece.Down, -1);
        for (int c = 0; c < N; c++) piece.Edge[c] = EdgeFade(face[c]);
        int Sample(int column) // a cell's middle sample in the window, or -1
        {
            int i = (hydrology.X0 + column % hydrology.Nx * Hydrology.Cell + Hydrology.Cell / 2) / Step - piece.X0;
            int k = (hydrology.Z0 + column / hydrology.Nx * Hydrology.Cell + Hydrology.Cell / 2) / Step - piece.Z0;
            return i < 0 || k < 0 || i >= Window || k >= Window ? -1 : i + Window * k;
        }
        var rivers = new List<int>();
        for (int k = 0; k < Window; k++)
        for (int i = 0; i < Window; i++)
        {
            int c = i + Window * k;
            if (ground[c] == Drain) continue;
            int column = hydrology.ColumnAt((piece.X0 + i + 0.5f) * Step, (piece.Z0 + k + 0.5f) * Step);
            if (column < 0) continue;
            float lake = hydrology.Lake[column];
            if (!float.IsNaN(lake))
            {
                int level = (int)MathF.Floor(lake) - 1;
                if (ground[c] < level) piece.Lake[c] = level;
            }
            else if (hydrology.Flow[column] >= RiverFrom && Sample(column) == c) rivers.Add(c);
        }

        // Each river's level is a block under its ground, never rising on its way (worked from upstream down), and not
        // under a lake it meets.
        var erosion = Erosion.For(_seed);
        foreach (int c in rivers) piece.River[c] = int.MaxValue; // (worked out below)
        int ColumnOf(int c) => hydrology.ColumnAt((piece.X0 + c % Window + 0.5f) * Step, (piece.Z0 + c / Window + 0.5f) * Step);
        foreach (int c in rivers)
        {
            int column = ColumnOf(c), d = hydrology.Down[column] >= 0 ? Sample(hydrology.Down[column]) : -1;
            piece.Down[c] = d >= 0 && ground[d] != Drain ? d : -1;
        }
        // (Upstream first: the furthest from where its river ends.)
        var along = new Dictionary<int, int>();
        int Along(int c)
        {
            if (along.TryGetValue(c, out int a)) return a;
            along[c] = 0; // (so a loop ends)
            int d = piece.Down[c];
            return along[c] = d >= 0 && piece.River[d] != int.MinValue ? Along(d) + 1 : 0;
        }
        rivers.Sort((a, b) => Along(b).CompareTo(Along(a)));
        foreach (int c in rivers)
        {
            int column = ColumnOf(c), ci = c % Window, ck = c / Window, d = piece.Down[c];
            int lakeBeside = int.MinValue;
            for (int k = Math.Max(0, ck - 2); k <= Math.Min(Window - 1, ck + 2); k++)
            for (int i = Math.Max(0, ci - 2); i <= Math.Min(Window - 1, ci + 2); i++)
                lakeBeside = Math.Max(lakeBeside, piece.Lake[i + Window * k]);
            piece.River[c] = Math.Max(Math.Min(piece.River[c], (int)MathF.Floor(ground[c]) - 1), lakeBeside);
            float vary = 1f + RiverVary * _widths.GetNoise((piece.X0 + ci) * Step, (piece.Z0 + ck) * Step);
            float flow = hydrology.Flow[column];
            float width = Math.Clamp(MathF.Round((RiverNarrowest + RiverWiden * MathF.Sqrt(flow - RiverFrom)) * vary), 2f, RiverWidest);
            // (In a channel the land was worn for, as wide as it, less a block either side.)
            float sx = (piece.X0 + ci + 0.5f) * Step, sz = (piece.Z0 + ck + 0.5f) * Step;
            erosion.Of(sx, sz).Channel(sx, sz, out float channel);
            piece.Width[c] = (byte)MathF.Max(width, channel - 2f);
            if (d >= 0 && piece.River[d] != int.MinValue) piece.River[d] = Math.Min(piece.River[d], piece.River[c]);
        }
        // The cells' ways go only straight across, so a river drawn cell to cell would go in steps: each sample's line
        // runs on to the sample two on instead, cutting across the corners.
        var next = (int[])piece.Down.Clone();
        foreach (int c in rivers)
        {
            int d = next[c];
            if (d >= 0 && piece.River[d] != int.MinValue && next[d] >= 0) piece.Down[c] = next[d];
        }
        Floats.Return(face);
        return piece;
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
    /// <summary>The top row's heart whose piece holds (x, z) at the surface, and its cell (hi, hk); null if none.</summary>
    internal Heart? Nearest(float x, float z, out int hi, out int hk)
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

    // Asked per block column: the lambdas are static, given what they need, so a lookup allocates nothing (one capturing
    // hi and hk allocated a closure and a delegate every call, most of what generating terrain allocated).
    private Heart TopHeart(int i, int k)
        => _hearts.GetOrAdd(Key(i, k), static (_, a) => HeartGrid.At(a.Seed, 0, a.I, -1, a.K), (Seed: _seed, I: i, K: k));

    private Piece PieceOf(int hi, int hk)
        => _pieces.GetOrAdd(Key(hi, hk), static (_, a) => a.Water.Build(a.I, a.K), (Water: this, I: hi, K: hk));

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
