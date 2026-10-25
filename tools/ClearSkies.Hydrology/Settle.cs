using System.Numerics;

namespace ClearSkies.Hydrology;

/// <summary>What the water model's materials are like. Capacity is how much water a cell holds (open air holds 1);
/// permeability is the share of a head difference (in cells) that crosses a face in one pass.</summary>
public sealed record Materials(float GroundCapacity = 2f, float AirPermeability = 0.15f, float FallPermeability = 0.5f,
                               float SoilPermeability = 0.01f, float StonePermeability = 0.0005f, int SoilDepth = 1);

/// <summary>
/// The uniform water model on a <see cref="CoarseLand"/>: every cell holds water up to its capacity, and has a head,
/// the height its water stands to (in blocks): its bottom plus how full it is, or where it's full under more water,
/// the head of the water over it (the pressure). Each pass, water moves across every face from the higher head to the
/// lower, the permeability of that face times the difference, never more than a cell holds. Rain falls on every
/// column's ground; water leaving the land (off its edge or out of its bottom) is gone.
/// A pass is four sweeps over all columns on all cores, the way GPU compute passes would be: down each column, falls
/// and heads; the flow across each cell's +x, +z and upper faces (and out of it into the void); how much of what each
/// cell would send it can; and the new water. A column's cells are contiguous, and so is the run of them beside any
/// neighbouring column's, so the last three sweeps go a SIMD vector of cells at a time.
/// </summary>
public sealed class Settle
{
    private const float PerBlock = 1f / CoarseLand.Size;

    private readonly CoarseLand _land;
    private readonly float[] _capacity, _permeability, _head, _scale, _drain;
    private readonly float[] _fx, _fz, _fy; // flow out across +x, +z and up (negative: in)
    private float[] _water, _next;
    private readonly int[] _rainCell;
    private readonly int _raining;
    private readonly float _fall, _airish;

    public float Rain { get; set; }

    /// <summary>Whether free water falls through open air all the way in one pass, rather than a cell or so.</summary>
    public bool Falls { get; set; } = true;

    public double Stored, RainedLastPass, DrainedLastPass;

    public ReadOnlySpan<float> Water => _water;
    public float Capacity(int i) => _capacity[i];

    public Settle(CoarseLand land, Materials m, float rain)
    {
        _land = land;
        Rain = rain;
        int n = land.Cells;
        _capacity = new float[n]; _permeability = new float[n]; _head = new float[n]; _scale = new float[n];
        _drain = new float[n]; _fx = new float[n]; _fz = new float[n]; _fy = new float[n];
        _water = new float[n]; _next = new float[n];
        _rainCell = new int[land.Columns];
        Parallel.For(0, land.Columns, c =>
        {
            _rainCell[c] = -1;
            if (land.Count(c) == 0) return;
            for (int y = land.Lo[c]; y <= land.Hi[c]; y++)
            {
                int i = land.Index(c, y);
                float s = land.Solid[i];
                _capacity[i] = (1f - s) + s * m.GroundCapacity;
                float ground = land.Top[c] - y < m.SoilDepth + 1 ? m.SoilPermeability : m.StonePermeability;
                // Partly solid cells blend geometrically: a thin floor of ground slows water nearly as much as a full one.
                _permeability[i] = MathF.Exp((1f - s) * MathF.Log(m.AirPermeability) + s * MathF.Log(ground));
            }
            int top = land.Index(c, land.Top[c]);
            _rainCell[c] = land.Solid[top] < 1f ? top : land.Index(c, land.Top[c] + 1);
        });
        foreach (int r in _rainCell) if (r >= 0) _raining++;
        _fall = m.FallPermeability / m.AirPermeability;
        _airish = m.AirPermeability * 0.5f;
    }

    /// <summary>Starts the ground full of water up to <paramref name="depth"/> cells under each column's top, rather than
    /// dry, so it doesn't take thousands of passes to soak in.</summary>
    public void Saturate(int depth)
    {
        var land = _land;
        Parallel.For(0, land.Columns, c =>
        {
            for (int y = land.Lo[c]; y <= System.Math.Min(land.Hi[c], land.Top[c] - depth); y++)
            {
                int i = land.Index(c, y);
                if (land.Solid[i] > 0f) _water[i] = _capacity[i];
            }
        });
    }

    /// <summary>Starts every hollow in the land's top full of water to where it would spill (a priority flood in from
    /// the land's edges and cracks), so lakes don't have to fill a raindrop at a time. Returns how many columns hold
    /// some.</summary>
    public int FillHollows()
    {
        var land = _land;
        int nx = land.Nx, nz = land.Nz;
        var level = new float[land.Columns];
        var queue = new PriorityQueue<int, float>();
        for (int c = 0; c < land.Columns; c++)
        {
            level[c] = float.NaN;
            if (land.Count(c) == 0) continue;
            int i = c % nx, j = c / nx;
            bool edge = i == 0 || j == 0 || i == nx - 1 || j == nz - 1
                     || land.Count(c - 1) == 0 || land.Count(c + 1) == 0 || land.Count(c - nx) == 0 || land.Count(c + nx) == 0;
            if (!edge) continue;
            level[c] = Surface(c);
            queue.Enqueue(c, level[c]);
        }
        while (queue.TryDequeue(out int c, out float l))
        {
            int i = c % nx, j = c / nx;
            foreach (int n in stackalloc[] { i + 1 < nx ? c + 1 : -1, i > 0 ? c - 1 : -1, j + 1 < nz ? c + nx : -1, j > 0 ? c - nx : -1 })
            {
                if (n < 0 || land.Count(n) == 0 || !float.IsNaN(level[n])) continue;
                level[n] = MathF.Max(Surface(n), l);
                queue.Enqueue(n, level[n]);
            }
        }
        int wet = 0;
        for (int c = 0; c < land.Columns; c++)
        {
            if (float.IsNaN(level[c]) || level[c] <= Surface(c) + 0.5f) continue;
            wet++;
            for (int y = land.Top[c]; y <= land.Hi[c]; y++)
            {
                int k = land.Index(c, y);
                float bottom = CoarseLand.Size * (y + land.Solid[k]);
                float open = MathF.Min(level[c], CoarseLand.Size * (y + 1)) - bottom;
                if (open <= 0f) break;
                // Its ground soaked, and its open part filled to the level.
                float ground = _capacity[k] - (1f - land.Solid[k]);
                _water[k] = MathF.Min(_capacity[k], ground + open / CoarseLand.Size);
            }
        }
        return wet;
    }

    /// <summary>The height (blocks) of column c's top: its top cell's bottom plus the ground in it.</summary>
    private float Surface(int c) => CoarseLand.Size * (_land.Top[c] + _land.Solid[_land.Index(c, _land.Top[c])]);

    public void Pass()
    {
        var land = _land;
        int nx = land.Nx, nz = land.Nz;
        // 1. Down each column: free water falls to the bottom of its run of open air (all the way, in one pass), then
        // heads: a full cell under water stands to that water's head.
        Parallel.For(0, land.Columns, c =>
        {
            if (Falls)
                for (int y = land.Hi[c]; y >= land.Lo[c];)
                {
                    int i = land.Start[c] + y - land.Lo[c];
                    if (land.Solid[i] > 0f) { y--; continue; }
                    int top = i;
                    float carry = 0f;
                    for (; y >= land.Lo[c] && land.Solid[i] == 0f; y--, i--) { carry += _water[i]; _water[i] = 0f; }
                    for (int r = i + 1; r <= top && carry > 0f; r++)
                    {
                        float put = MathF.Min(carry, _capacity[r]);
                        _water[r] = put;
                        carry -= put;
                    }
                    _water[top] += carry;
                }
            float above = float.NaN;
            for (int y = land.Hi[c]; y >= land.Lo[c]; y--)
            {
                int i = land.Start[c] + y - land.Lo[c];
                float fill = _water[i] / _capacity[i];
                float h = CoarseLand.Size * (y + fill);
                if (fill >= 1f && !float.IsNaN(above)) h = MathF.Max(h, above);
                _head[i] = h;
                above = _water[i] > 0f ? h : float.NaN;
            }
        });
        // 2. Flow across each cell's +x, +z and upper faces, and out into the void across any face.
        Parallel.For(0, nz, j =>
        {
            for (int ci = 0; ci < nx; ci++)
            {
                int c = ci + nx * j, n = land.Count(c);
                if (n == 0) continue;
                int s = land.Start[c];
                _drain.AsSpan(s, n).Clear();
                Side(c, ci + 1 < nx ? c + 1 : -1, _fx);
                Side(c, j + 1 < nz ? c + nx : -1, _fz);
                Side(c, ci > 0 ? c - 1 : -1, null);
                Side(c, j > 0 ? c - nx : -1, null);
                Vertical(s, n - 1);
                _fy[s + n - 1] = 0f;
                Drain(s, 1, land.Lo[c], down: true);
            }
        });
        // 3. The share of what each cell would send out that it can.
        Parallel.For(0, nz, j =>
        {
            for (int ci = 0; ci < nx; ci++)
            {
                int c = ci + nx * j, n = land.Count(c);
                if (n == 0) continue;
                int s = land.Start[c];
                var sum = _scale.AsSpan(s, n);
                Outflow(sum, _drain.AsSpan(s, n), _fx.AsSpan(s, n), _fz.AsSpan(s, n), _fy.AsSpan(s, n));
                if (ci > 0 && Overlap(c, c - 1, out int m1, out int t1, out int l1)) Backflow(_scale.AsSpan(m1, l1), _fx.AsSpan(t1, l1));
                if (j > 0 && Overlap(c, c - nx, out int m2, out int t2, out int l2)) Backflow(_scale.AsSpan(m2, l2), _fz.AsSpan(t2, l2));
                Backflow(_scale.AsSpan(s + 1, n - 1), _fy.AsSpan(s, n - 1));
                Share(sum, _water.AsSpan(s, n));
            }
        });
        // 4. Each cell loses what it sends and gathers what its neighbours send it.
        double drained = 0, stored = 0;
        var gate = new object();
        Parallel.For(0, nz, () => (0.0, 0.0), (j, state, acc) =>
        {
            for (int ci = 0; ci < nx; ci++)
            {
                int c = ci + nx * j, n = land.Count(c);
                if (n == 0) continue;
                int s = land.Start[c];
                var next = _next.AsSpan(s, n);
                acc.Item1 += Start(next, _water.AsSpan(s, n), _scale.AsSpan(s, n), _drain.AsSpan(s, n));
                if (ci + 1 < nx && Overlap(c, c + 1, out int m3, out int t3, out int l3)) Move(_next.AsSpan(m3, l3), _fx.AsSpan(m3, l3), _scale.AsSpan(m3, l3), _scale.AsSpan(t3, l3), -1f);
                if (j + 1 < nz && Overlap(c, c + nx, out int m4, out int t4, out int l4)) Move(_next.AsSpan(m4, l4), _fz.AsSpan(m4, l4), _scale.AsSpan(m4, l4), _scale.AsSpan(t4, l4), -1f);
                if (ci > 0 && Overlap(c, c - 1, out int m5, out int t5, out int l5)) Move(_next.AsSpan(m5, l5), _fx.AsSpan(t5, l5), _scale.AsSpan(t5, l5), _scale.AsSpan(m5, l5), 1f);
                if (j > 0 && Overlap(c, c - nx, out int m6, out int t6, out int l6)) Move(_next.AsSpan(m6, l6), _fz.AsSpan(t6, l6), _scale.AsSpan(t6, l6), _scale.AsSpan(m6, l6), 1f);
                Move(_next.AsSpan(s, n - 1), _fy.AsSpan(s, n - 1), _scale.AsSpan(s, n - 1), _scale.AsSpan(s + 1, n - 1), -1f);
                Move(_next.AsSpan(s + 1, n - 1), _fy.AsSpan(s, n - 1), _scale.AsSpan(s, n - 1), _scale.AsSpan(s + 1, n - 1), 1f);
                if (_rainCell[c] >= 0) _next[_rainCell[c]] += Rain;
                acc.Item2 += Clamp(next);
            }
            return acc;
        }, acc => { lock (gate) { drained += acc.Item1; stored += acc.Item2; } });
        (_water, _next) = (_next, _water);
        RainedLastPass = _raining * (double)Rain;
        DrainedLastPass = drained;
        Stored = stored;
    }

    /// <summary>The cells column c has beside column nc's: the first of each, and how many.</summary>
    private bool Overlap(int c, int nc, out int mine, out int theirs, out int len)
    {
        var land = _land;
        mine = theirs = len = 0;
        if (land.Count(nc) == 0) return false;
        int lo = System.Math.Max(land.Lo[c], land.Lo[nc]), hi = System.Math.Min(land.Hi[c], land.Hi[nc]);
        if (lo > hi) return false;
        mine = land.Start[c] + lo - land.Lo[c]; theirs = land.Start[nc] + lo - land.Lo[nc]; len = hi - lo + 1;
        return true;
    }

    /// <summary>The side face of column c towards column nc (-1: off the land's bounds): flow across it into f (or
    /// none for a face the other column keeps), and into the void where there is no land beside or under it there.
    /// Above the column beside is a wall.</summary>
    private void Side(int c, int nc, float[]? f)
    {
        var land = _land;
        int lo = land.Lo[c], hi = land.Hi[c], s = land.Start[c];
        int voidTop = nc < 0 || land.Count(nc) == 0 ? hi : System.Math.Min(hi, land.Lo[nc] - 1);
        if (voidTop >= lo) Drain(s, voidTop - lo + 1, lo, down: false);
        if (f == null) return;
        f.AsSpan(s, land.Count(c)).Clear();
        if (nc >= 0 && Overlap(c, nc, out int m7, out int t7, out int l7)) Flux(f.AsSpan(m7, l7), m7, t7, l7);
    }

    // ── The vector loops ──────────────────────────────────────────────────

    private void Flux(Span<float> f, int i, int n, int len)
    {
        var ki = _permeability.AsSpan(i, len); var kn = _permeability.AsSpan(n, len);
        var hi = _head.AsSpan(i, len); var hn = _head.AsSpan(n, len);
        int v = 0, w = Vector<float>.Count;
        for (; v <= len - w; v += w)
            (Vector.Min(new Vector<float>(ki[v..]), new Vector<float>(kn[v..])) * (new Vector<float>(hi[v..]) - new Vector<float>(hn[v..])) * PerBlock).CopyTo(f[v..]);
        for (; v < len; v++) f[v] = MathF.Min(ki[v], kn[v]) * (hi[v] - hn[v]) * PerBlock;
    }

    /// <summary>Flow up from each of cells s.. (count) to the one over it; water falls through open air faster.</summary>
    private void Vertical(int s, int count)
    {
        if (count <= 0) return;
        var k0 = _permeability.AsSpan(s, count); var k1 = _permeability.AsSpan(s + 1, count);
        var h0 = _head.AsSpan(s, count); var h1 = _head.AsSpan(s + 1, count);
        var f = _fy.AsSpan(s, count);
        var airish = new Vector<float>(_airish); var fall = new Vector<float>(_fall);
        int v = 0, w = Vector<float>.Count;
        for (; v <= count - w; v += w)
        {
            var k = Vector.Min(new Vector<float>(k0[v..]), new Vector<float>(k1[v..]));
            var dh = new Vector<float>(h0[v..]) - new Vector<float>(h1[v..]);
            var falling = Vector.LessThan(dh, Vector<float>.Zero) & Vector.GreaterThanOrEqual(k, airish);
            (Vector.ConditionalSelect(falling, k * fall, k) * dh * PerBlock).CopyTo(f[v..]);
        }
        for (; v < count; v++)
        {
            float k = MathF.Min(k0[v], k1[v]), dh = h0[v] - h1[v];
            if (dh < 0f && k >= _airish) k *= _fall;
            f[v] = k * dh * PerBlock;
        }
    }

    /// <summary>Adds the flow out of cells s.. (count, the first at height y0) into the void: empty air a cell below.</summary>
    private void Drain(int s, int count, int y0, bool down)
    {
        var k = _permeability.AsSpan(s, count); var h = _head.AsSpan(s, count); var d = _drain.AsSpan(s, count);
        for (int v = 0; v < count; v++)
        {
            float kk = k[v], dh = h[v] - CoarseLand.Size * (y0 + v - 1);
            if (down && kk >= _airish) kk *= _fall;
            if (dh > 0f) d[v] += kk * dh * PerBlock;
        }
    }

    private static void Outflow(Span<float> sum, ReadOnlySpan<float> drain, ReadOnlySpan<float> fx, ReadOnlySpan<float> fz, ReadOnlySpan<float> fy)
    {
        int v = 0, w = Vector<float>.Count, len = sum.Length;
        for (; v <= len - w; v += w)
            (new Vector<float>(drain[v..]) + Vector.Max(new Vector<float>(fx[v..]), Vector<float>.Zero)
             + Vector.Max(new Vector<float>(fz[v..]), Vector<float>.Zero) + Vector.Max(new Vector<float>(fy[v..]), Vector<float>.Zero)).CopyTo(sum[v..]);
        for (; v < len; v++) sum[v] = drain[v] + MathF.Max(fx[v], 0f) + MathF.Max(fz[v], 0f) + MathF.Max(fy[v], 0f);
    }

    /// <summary>Adds what a cell sends across a face its neighbour keeps (that neighbour's flow, negated).</summary>
    private static void Backflow(Span<float> sum, ReadOnlySpan<float> f)
    {
        int v = 0, w = Vector<float>.Count, len = sum.Length;
        for (; v <= len - w; v += w)
            (new Vector<float>(sum[v..]) + Vector.Max(-new Vector<float>(f[v..]), Vector<float>.Zero)).CopyTo(sum[v..]);
        for (; v < len; v++) sum[v] += MathF.Max(-f[v], 0f);
    }

    /// <summary>Turns what each cell would send into the share of it that it can.</summary>
    private static void Share(Span<float> sum, ReadOnlySpan<float> water)
    {
        int v = 0, w = Vector<float>.Count, len = sum.Length;
        for (; v <= len - w; v += w)
        {
            var s = new Vector<float>(sum[v..]); var wv = new Vector<float>(water[v..]);
            var share = Vector.ConditionalSelect(Vector.GreaterThan(s, wv), wv / s, Vector<float>.One);
            Vector.ConditionalSelect(Vector.GreaterThan(wv, Vector<float>.Zero), share, Vector<float>.Zero).CopyTo(sum[v..]);
        }
        for (; v < len; v++) sum[v] = water[v] <= 0f ? 0f : sum[v] > water[v] ? water[v] / sum[v] : 1f;
    }

    /// <summary>The new water before any flow between cells: the old less what drains into the void. Returns that.</summary>
    private static double Start(Span<float> next, ReadOnlySpan<float> water, ReadOnlySpan<float> scale, ReadOnlySpan<float> drain)
    {
        double drained = 0;
        for (int v = 0; v < next.Length; v++)
        {
            float d = scale[v] * drain[v];
            next[v] = water[v] - d;
            drained += d;
        }
        return drained;
    }

    /// <summary>Adds sign times what crosses a face with flow f (out of its keeper, whose scale is from), the receiver's
    /// scale applying to flow the other way.</summary>
    private static void Move(Span<float> next, ReadOnlySpan<float> f, ReadOnlySpan<float> from, ReadOnlySpan<float> to, float sign)
    {
        int v = 0, w = Vector<float>.Count, len = next.Length;
        var sv = new Vector<float>(sign);
        for (; v <= len - w; v += w)
        {
            var fv = new Vector<float>(f[v..]);
            var moved = fv * Vector.ConditionalSelect(Vector.GreaterThan(fv, Vector<float>.Zero), new Vector<float>(from[v..]), new Vector<float>(to[v..]));
            (new Vector<float>(next[v..]) + sv * moved).CopyTo(next[v..]);
        }
        for (; v < len; v++) next[v] += sign * f[v] * (f[v] > 0f ? from[v] : to[v]);
    }

    private static double Clamp(Span<float> next)
    {
        double total = 0;
        for (int v = 0; v < next.Length; v++) { if (next[v] < 0f) next[v] = 0f; total += next[v]; }
        return total;
    }
}
