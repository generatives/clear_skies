using ClearSkies.Engine.Generation;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Reading a landmass's model for blocks (see HeartWorldGenerator): each column's top floor before and after, the water
/// over it, and its stream (see HeartWorldGenerator.PlaceWater). The first Damp blocks of water only dampen the ground:
/// thin water running over a slope shows as none.
/// </summary>
public sealed partial class CoarseLandmass
{
    // (How far ground on a stream's line may stand over its line's ground and still be cut through; how deep a
    // channel is cut at most.)
    private const float Hump = 3f, MostDeep = 4f;
    /// <summary>How deep (past the damp) the model's water must be to show as its own surface: shallower, it runs in
    /// its stream's channel (a channel that deep would be a column wide).</summary>
    public const float Shallow = 1f;
    // (How deep the shown water blended over a block column must be for it to show there, see WetLevel.)
    private const float ShowsFrom = 0.25f;
    // (How narrow a channel is at least.)
    private const float LeastWide = 2f;

    /// <summary>The top water block over block column (x, z), whose ground is topped at <paramref name="top"/>, or
    /// int.MinValue, and the block its ground is cut down to (int.MaxValue for none). Full detail and distant terrain
    /// both place water by this (distant terrain <paramref name="widen"/>s by half a voxel, so water thinner than a voxel
    /// still shows, and cuts nothing). Two rules:
    /// - the model's own surface, a block lower, blended between the nearest columns so it thins into the ground
    ///   wherever it ends (see WetLevel): a river fills its valley as the model has it, and a lake is level. Its water
    ///   takes the top ground block's place (a block cut where it's that shallow), so its edges sit level with the
    ///   ground beside;
    /// - every stream runs in a channel cut along its curve (see Curve): the model's water across its column (the
    ///   column's width times its depth) gathered three times as wide as it's deep (LeastWide wide and a block deep at
    ///   least, a column wide and MostDeep deep at most), its bed rounded and its surface level with the line's ground,
    ///   a block down. Ground up to Hump over the line is cut through, so it runs on over small rises; down a steep
    ///   drop (its curve falling more than half a column) it runs over the ground along its line, uncut, no higher
    ///   than the drop's top. Under the model's own surface nothing is cut.</summary>
    public (int Water, int Bed) Water(int x, int z, int top, float widen)
    {
        // (The shown surface is a block under the model's: its water takes the top ground block's place, as a stream's
        // does, so a shallow edge sits level with the ground beside it, not a block over it; distant terrain, which
        // doesn't cut, keeps it over its voxel.)
        float shown = WetLevel(x, z, top);
        int size = Size, bed = int.MaxValue, r = 2 + (int)MathF.Ceiling(widen / size);
        int water = float.IsNaN(shown) ? int.MinValue : (int)MathF.Floor(shown + (widen > 0f ? 0.5f : -0.5f));
        if (water < top || (widen > 0f && water == top)) water = int.MinValue;
        bool shows = water != int.MinValue;
        if (shows && widen <= 0f && water <= top) bed = water - 1;
        int ic = (int)MathF.Round((x - X0) / (float)size), kc = (int)MathF.Round((z - Z0) / (float)size);
        float px = x + 0.5f, pz = z + 0.5f;
        Span<CurvePoint> curve = stackalloc CurvePoint[3];
        for (int k = kc - r; k <= kc + r; k++)
        for (int i = ic - r; i <= ic + r; i++)
        {
            if (i < 0 || k < 0 || i >= Nx || k >= Nz) continue;
            int t = TopNode(i + Nx * k);
            if (t < 0 || Shown[t] <= 0 || Way[t] == (0, 0)) continue;
            Curve(t, curve);
            // (The nearest point of its curve, a few straight pieces along it.)
            const int Pieces = 6;
            float best2 = float.MaxValue, bestAt = 0f;
            var prev = Curves.At(curve, 0f);
            for (int q = 1; q <= Pieces; q++)
            {
                var next = Curves.At(curve, q / (float)Pieces);
                float dx = next.X - prev.X, dz = next.Z - prev.Z, length2 = dx * dx + dz * dz;
                float along = length2 <= 0 ? 0f : Math.Clamp(((px - prev.X) * dx + (pz - prev.Z) * dz) / length2, 0f, 1f);
                float ox = px - prev.X - along * dx, oz = pz - prev.Z - along * dz;
                if (ox * ox + oz * oz < best2) { best2 = ox * ox + oz * oz; bestAt = (q - 1 + along) / Pieces; }
                prev = next;
            }
            var on = Curves.At(curve, bestAt);
            float g = on.Floor, highest = MathF.Max(curve[0].Floor, MathF.Max(curve[1].Floor, curve[2].Floor));
            float lowest = MathF.Min(curve[0].Floor, MathF.Min(curve[1].Floor, curve[2].Floor));
            bool steep = highest - lowest > size / 2f;
            if (top < g - size || top > (steep ? highest : g) - 1 + Hump) continue; // (land far below its line: past its edge; or a hill)
            float area = size * MathF.Max(0f, on.Depth);
            float width = Math.Clamp(MathF.Sqrt(3f * area) * on.Wide, LeastWide, size), deep = Math.Clamp(1.5f * area / width, 1f, MostDeep);
            float half = width / 2 + widen, off2 = best2;
            if (off2 > half * half) continue;
            if (top > g - 1 + Hump)
            {
                if (off2 > 1f) continue; // (only down its line itself, down a steep drop: not up its banks)
                // (Ground over its line's down a steep drop, no higher than its top: it runs down over it, uncut.)
                water = Math.Max(water, top + 1);
                continue;
            }
            int surface = (int)MathF.Round(g) - 1; // (the line's top ground block: water takes its place)
            water = Math.Max(water, widen > 0f ? Math.Max(surface, top + 1) : surface); // (distant terrain: over its voxel)
            if (widen <= 0f && !shows) // (under water the model already shows, nothing is cut)
                bed = Math.Min(bed, surface - Math.Max(1, (int)MathF.Round(deep * (1f - off2 / (half * half)))));
        }
        if (water <= Math.Min(top, bed)) return (int.MinValue, int.MaxValue);
        return (water, bed < top ? bed : int.MaxValue);
    }


    /// <summary>Node <paramref name="n"/>'s stream's line, as the three points of a curve (a quadratic Bézier): from
    /// halfway between it and its main stream in (its own middle, bent by its meander, at a head) round its middle to
    /// halfway to where it runs (where the stream it joins passes, if it's not that one's main stream in; the middle of
    /// the column past the land's edge if it runs off). One stream's curves meet end to end, without corners.</summary>
    private void Curve(int n, Span<CurvePoint> curve)
    {
        var b = Here(n);
        int u = Up[n], d = Down[n];
        curve[0] = u < 0 ? b : Curves.Half(Here(u), b);
        curve[1] = b;
        if (d < 0)
        {
            var (mx, mz) = Middle(n);
            curve[2] = new CurvePoint(mx + Way[n].X * Size, mz + Way[n].Z * Size, b.Floor, b.Depth, b.Wide);
        }
        else if (Up[d] == n || Shown[d] <= 0) curve[2] = Curves.Half(b, Here(d));
        else
        {
            Span<CurvePoint> joined = stackalloc CurvePoint[3];
            Curve3(d, joined);
            curve[2] = Curves.At(joined, 0.5f);
        }
    }

    /// <summary>Curve without the joining case (the stream joined's own curve, whose ends don't depend on others').</summary>
    private void Curve3(int n, Span<CurvePoint> curve)
    {
        var b = Here(n);
        int u = Up[n], d = Down[n];
        curve[0] = u < 0 ? b : Curves.Half(Here(u), b);
        curve[1] = b;
        if (d < 0) { var (mx, mz) = Middle(n); curve[2] = new CurvePoint(mx + Way[n].X * Size, mz + Way[n].Z * Size, b.Floor, b.Depth, b.Wide); }
        else curve[2] = Curves.Half(b, Here(d));
    }

    private CurvePoint Here(int n) { var (x, z) = Bent(n); return new CurvePoint(x, z, Floor[n], Depth[n], Wide[n]); }

    /// <summary>The surface the model's water shows over block column (x, z), a block under its own (Shallow past
    /// the damp), or NaN: each of the four nearest columns' shown water (none where it's that shallow) on its floor,
    /// blended by how near each is, shown where that's at least ShowsFrom deep. Every column counts alike, wet or dry, so
    /// the surface runs down into the ground toward dry columns as the water thins, wherever it ends, rather than in a
    /// face on the grid's lines; a dry column's ground counts no higher than the highest wet one's surface (water doesn't
    /// climb hills), and a column counts only over ground (<paramref name="top"/>) no more than a column under its floor
    /// (not past its edge).</summary>
    private float WetLevel(int x, int z, int top)
    {
        var (i0, k0, fx, fz) = Cell(x, z);
        float highest = float.NaN, past = Settings.Damp + Shallow;
        Span<int> corner = stackalloc int[4];
        for (int c = 0; c < 4; c++)
        {
            int t = TopNode(i0 + (c & 1), k0 + (c >> 1));
            corner[c] = t = t < 0 || top < Floor[t] - Size ? -1 : t;
            if (t >= 0 && Depth[t] > past && !(Floor[t] + Depth[t] - past <= highest)) highest = Floor[t] + Depth[t] - past;
        }
        if (float.IsNaN(highest)) return float.NaN;
        float sum = 0f, deep = 0f, weights = 0f;
        for (int c = 0; c < 4; c++)
        {
            int t = corner[c];
            if (t < 0) continue;
            float w = ((c & 1) == 0 ? 1 - fx : fx) * ((c >> 1) == 0 ? 1 - fz : fz) + 1e-4f, d = MathF.Max(0f, Depth[t] - past);
            sum += w * MathF.Min(Floor[t] + d, highest); deep += w * d; weights += w;
        }
        return deep < ShowsFrom * weights ? float.NaN : sum / weights;
    }

    /// <summary>The stream of the column holding (x, z): how wide it runs (sqrt(flow / StreamFlow) blocks; 0 for
    /// none), its line, from the column's middle bent across its way by the column's meander to the same point of the
    /// column it runs to (in blocks, block centres at .5), and the ground at both ends. A stream that runs over the
    /// land's edge runs on level to the middle of the column past it. Head: no stream runs into it (it springs here).</summary>
    public (float Width, float FromX, float FromZ, float ToX, float ToZ, float From, float To, bool Head) Stream(int x, int z)
    {
        int t = TopNodeAt(x, z);
        if (t < 0 || Settings.StreamFlow <= 0 || Shown[t] <= 0f || Way[t] == (0, 0)) return default;
        float width = MathF.Sqrt(Shown[t] / Settings.StreamFlow);
        var (fx, fz) = Bent(t);
        if (Down[t] < 0)
        {
            var (mx, mz) = Middle(t);
            return (width, fx, fz, mx + Way[t].X * Size, mz + Way[t].Z * Size, Floor[t], Floor[t], Head[t]);
        }
        var (tx, tz) = Bent(Down[t]);
        return (width, fx, fz, tx, tz, Floor[t], Floor[Down[t]], Head[t]);
    }

    /// <summary>The middle of node <paramref name="n"/>'s column (a block centre).</summary>
    private (float X, float Z) Middle(int n)
    {
        int c = ColumnOf[n], size = Size;
        return (X0 + c % Nx * size + 0.5f, Z0 + c / Nx * size + 0.5f);
    }

    /// <summary>Where node <paramref name="n"/>'s stream passes through its column: the middle, bent across its way by
    /// its meander.</summary>
    private (float X, float Z) Bent(int n)
    {
        var (mx, mz) = Middle(n);
        var (wx, wz) = Way[n];
        if (wx == 0 && wz == 0) return (mx, mz);
        float length = MathF.Sqrt(wx * wx + wz * wz);
        return (mx - wz / length * Bend[n], mz + wx / length * Bend[n]);
    }

    /// <summary>How far the ground at lattice corner (x, z) (on the 8-block lines, the middle of a column) moved, if
    /// the column's top stood within Near blocks of <paramref name="top"/> (the corner's own top before); else 0.</summary>
    public float Shift(int x, int z, float top)
    {
        const float Near = 16f;
        int t = TopNodeAt(x, z);
        return t < 0 || MathF.Abs(StartFloor[t] - top) > Near ? 0f : Floor[t] - StartFloor[t];
    }

    /// <summary>How steep the ground is at block column (x, z), blocks up per block across: the slope of its four
    /// nearest columns' floors blended (0 where one has none).</summary>
    public float Steepness(int x, int z)
    {
        var (i0, k0, fx, fz) = Cell(x, z);
        Span<float> floors = stackalloc float[4];
        for (int corner = 0; corner < 4; corner++)
        {
            int t = TopNode(i0 + (corner & 1), k0 + (corner >> 1));
            if (t < 0) return 0f;
            floors[corner] = Floor[t];
        }
        float dx = ((1 - fz) * (floors[1] - floors[0]) + fz * (floors[3] - floors[2])) / Size;
        float dz = ((1 - fx) * (floors[2] - floors[0]) + fx * (floors[3] - floors[1])) / Size;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Whether any of the columns round block column (x, z) or beside those has water over it or a stream
    /// through it (water reaches that far across: a stream's channel, the model's surface blended between columns).</summary>
    public bool NearWater(int x, int z)
    {
        var (i, k, _, _) = Cell(x, z);
        return i >= 0 && k >= 0 && i < Nx && k < Nz && (_nearWater ??= NearWaterTable())[i + Nx * k];
    }

    // (Per cell, by its first column: whether any of the 4 by 4 columns round it is watered. Worked out when first
    // asked, once the water and streams are in.)
    private bool[]? _nearWater;

    private bool[] NearWaterTable()
    {
        var near = new bool[Columns];
        for (int column = 0; column < Columns; column++)
        {
            int t = TopNode(column);
            if (t < 0 || !(Depth[t] > Settings.Damp || Shown[t] > 0f)) continue;
            int i = column % Nx, k = column / Nx;
            for (int b = k - 2; b <= k + 1; b++)
            for (int a = i - 2; a <= i + 1; a++)
                if (a >= 0 && b >= 0 && a < Nx && b < Nz) near[a + Nx * b] = true;
        }
        return near;
    }

    /// <summary>The cell of columns block column (x, z) is in: its first column (i0, k0), and how far across it is toward
    /// the next ones (fx, fz, 0-1), for blending the four (i0..i0 + 1, k0..k0 + 1).</summary>
    private (int I0, int K0, float Fx, float Fz) Cell(float x, float z)
    {
        float gx = (x - X0) / Size, gz = (z - Z0) / Size;
        int i0 = (int)MathF.Floor(gx), k0 = (int)MathF.Floor(gz);
        return (i0, k0, gx - i0, gz - k0);
    }

    /// <summary>Column (i, k)'s top node, or -1 (none, or off the land).</summary>
    private int TopNode(int i, int k) => i < 0 || k < 0 || i >= Nx || k >= Nz ? -1 : TopNode(i + Nx * k);

    /// <summary>The top node of the column with its middle at (x, z), or -1.</summary>
    private int TopNodeAt(int x, int z)
    {
        int size = Size, i = (x - X0) / size, k = (z - Z0) / size;
        if (x < X0 || z < Z0 || i >= Nx || k >= Nz) return -1;
        return TopNode(i + Nx * k);
    }
}
