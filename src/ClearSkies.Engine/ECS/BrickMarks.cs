using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Accumulates boxes of bricks (brick coordinates, 4x4x4 bricks per chunk) as one 64-bit mask per loaded chunk (kept
/// on its record), then visits each marked brick's light slot once. Overlapping boxes (neighbouring sun-sweep layers,
/// the bounce reach around neighbouring bricks) cost a mask OR each instead of a walk over their bricks. Chunks with
/// no light storage (unloaded, or sky) cost one lookup. Only one BrickMarks may be filling at a time: fill, then flush.
/// </summary>
internal sealed class BrickMarks
{
    private readonly List<ChunkRecord> _marked = new();

    // Bits of a chunk's bricks with local x (y, z) in [a, b]: brick bit = x + 4 * (y + 4 * z).
    private static readonly ulong[] XRange = Ranges(1), YRange = Ranges(4), ZRange = Ranges(16);

    private static ulong[] Ranges(int stride)
    {
        var r = new ulong[16];
        for (int a = 0; a < 4; a++)
        for (int b = a; b < 4; b++)
        {
            ulong m = 0;
            for (int bit = 0; bit < 64; bit++)
            {
                int v = bit / stride % 4;
                if (v >= a && v <= b) m |= 1UL << bit;
            }
            r[a * 4 + b] = m;
        }
        return r;
    }

    public bool IsEmpty => _marked.Count == 0;

    /// <summary>Marks the inclusive brick box, clipped to <paramref name="g"/>'s chunk box.</summary>
    public void AddBox(GridHandle g, int bx0, int by0, int bz0, int bx1, int by1, int bz1)
    {
        if (bx0 > bx1 || by0 > by1 || bz0 > bz1 || !g.HasBox) return;
        int cx0 = System.Math.Max(bx0 >> 2, g.BoxMin.X), cx1 = System.Math.Min(bx1 >> 2, g.BoxMax.X);
        int cy0 = System.Math.Max(by0 >> 2, g.BoxMin.Y), cy1 = System.Math.Min(by1 >> 2, g.BoxMax.Y);
        int cz0 = System.Math.Max(bz0 >> 2, g.BoxMin.Z), cz1 = System.Math.Min(bz1 >> 2, g.BoxMax.Z);
        for (int cz = cz0; cz <= cz1; cz++)
        {
            ulong zm = ZRange[System.Math.Max(bz0 - cz * 4, 0) * 4 + System.Math.Min(bz1 - cz * 4, 3)];
            for (int cy = cy0; cy <= cy1; cy++)
            {
                ulong ym = zm & YRange[System.Math.Max(by0 - cy * 4, 0) * 4 + System.Math.Min(by1 - cy * 4, 3)];
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    ulong m = ym & XRange[System.Math.Max(bx0 - cx * 4, 0) * 4 + System.Math.Min(bx1 - cx * 4, 3)];
                    if (!g.Chunks.TryGetValue(new ChunkPosition(cx, cy, cz), out var rec) || rec.BrickSlots == null) continue;
                    if (rec.Marks == 0) _marked.Add(rec);
                    rec.Marks |= m;
                }
            }
        }
    }

    /// <summary>Calls <paramref name="action"/> once for the light slot of every marked brick that has one, and
    /// clears the marks.</summary>
    public void Flush(Action<int> action)
    {
        foreach (var rec in _marked)
        {
            ulong mask = rec.Marks;
            rec.Marks = 0;
            if (rec.BrickSlots is not { } slots) continue;
            for (ulong m = mask; m != 0; m &= m - 1)
            {
                int slot = slots[System.Numerics.BitOperations.TrailingZeroCount(m)];
                if (slot >= 0) action(slot);
            }
        }
        _marked.Clear();
    }
}
