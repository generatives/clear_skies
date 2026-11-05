using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Ruins;

public enum BoxOp : byte
{
    /// <summary>Brick shell (walls, floor and ceiling), air inside.</summary>
    Room,
    /// <summary>Brick floor and ceiling, air between, no side walls: halls tunnel through rock, and bridges are open
    /// at the sides.</summary>
    Hall,
    /// <summary>Air: doors and shafts.</summary>
    Air,
}

/// <summary>A box of blocks, X0..X1-1 and so on.</summary>
public readonly record struct BlockBox(int X0, int Y0, int Z0, int X1, int Y1, int Z1, BoxOp Op);

/// <summary>
/// A plan as boxes of blocks, indexed by the chunks they touch, so carving a chunk only looks at its own boxes. A chunk
/// is carved in two passes: first every box's brick (shells, floors, ceilings), then every box's air, so doors and
/// shafts cut through the brick of whatever they cross.
/// </summary>
public sealed class RuinCarving
{
    private const int S = ChunkData.Size;
    /// <summary>Doors: blocks wide and tall.</summary>
    public const int DoorWidth = 2, DoorHeight = 3;
    /// <summary>How high the entrance's tower stands over the ground.</summary>
    public const int TowerHeight = 6;

    public readonly BlockBox[] Boxes;
    public readonly Dictionary<long, int[]> ByChunk;

    public static long Key(int cx, int cy, int cz) => ((long)(cx & 0x1FFFFF) << 42) | ((long)(cy & 0x1FFFFF) << 21) | (uint)(cz & 0x1FFFFF);
    public static (int X, int Y, int Z) Unkey(long k)
    {
        static int Signed(long v) => (int)(v << 43 >> 43);
        return (Signed(k >> 42), Signed(k >> 21), Signed(k));
    }

    /// <summary>Bytes the boxes and index take, roughly.</summary>
    public long MemoryBytes => Boxes.Length * 28L + ByChunk.Sum(kv => 48L + kv.Value.Length * 4L);

    public RuinCarving(RuinPlan plan)
    {
        var site = plan.Site;
        const int C = RuinSite.Cell, P = RuinSite.FloorPitch;
        var boxes = new List<BlockBox>(plan.Parts.Count * 2);
        foreach (var p in plan.Parts)
        {
            int x0 = site.X0 + p.Rect.I0 * C, z0 = site.Z0 + p.Rect.J0 * C, x1 = site.X0 + p.Rect.I1 * C, z1 = site.Z0 + p.Rect.J1 * C;
            int y = site.FloorY(p.Floor);
            switch (p.Kind)
            {
                case PartKind.Plot:
                {
                    boxes.Add(new BlockBox(x0, y, z0, x1, y + P, z1, BoxOp.Room));
                    int cx = site.X0 + p.DoorI * C + C / 2, cz = site.Z0 + p.DoorJ * C + C / 2;
                    if (p.DoorDj != 0)
                    {
                        int wz = p.DoorDj > 0 ? z1 - 1 : z0;
                        boxes.Add(new BlockBox(cx - DoorWidth / 2, y + 1, wz, cx - DoorWidth / 2 + DoorWidth, y + 1 + DoorHeight, wz + 1, BoxOp.Air));
                    }
                    else
                    {
                        int wx = p.DoorDi > 0 ? x1 - 1 : x0;
                        boxes.Add(new BlockBox(wx, y + 1, cz - DoorWidth / 2, wx + 1, y + 1 + DoorHeight, cz - DoorWidth / 2 + DoorWidth, BoxOp.Air));
                    }
                    break;
                }
                case PartKind.Core:
                {
                    int bottom = site.FloorY(p.LastFloor);
                    boxes.Add(new BlockBox(x0, bottom + 1, z0, x1, y + P - 1, z1, BoxOp.Air));
                    if (p.Entrance)
                    {
                        // A brick tower from the top floor's ceiling up through the ground (so it doesn't wall off the top floor's halls),
                        // with a door on its -z side.
                        int surface = int.MinValue;
                        for (int j = p.Rect.J0; j < p.Rect.J1; j++)
                        for (int i = p.Rect.I0; i < p.Rect.I1; i++) surface = Math.Max(surface, site.Surface[i + site.Nx * j]);
                        int top = Math.Max(surface, y + P) + TowerHeight;
                        boxes.Add(new BlockBox(x0 - 1, y + P - 1, z0 - 1, x1 + 1, top + 1, z1 + 1, BoxOp.Room));
                        int mx = (x0 + x1) / 2;
                        boxes.Add(new BlockBox(mx - DoorWidth / 2, surface + 1, z0 - 1, mx - DoorWidth / 2 + DoorWidth, surface + 1 + DoorHeight, z0, BoxOp.Air));
                        // Its floor is the top floor's: open the shaft into it.
                        boxes.Add(new BlockBox(x0, y + 1, z0, x1, top, z1, BoxOp.Air));
                    }
                    break;
                }
                default:
                    boxes.Add(new BlockBox(x0, y, z0, x1, y + P, z1, BoxOp.Hall));
                    break;
            }
        }
        Boxes = boxes.ToArray();

        var index = new Dictionary<long, List<int>>();
        for (int b = 0; b < Boxes.Length; b++)
        {
            var box = Boxes[b];
            for (int cz = FloorDiv(box.Z0); cz <= FloorDiv(box.Z1 - 1); cz++)
            for (int cy = FloorDiv(box.Y0); cy <= FloorDiv(box.Y1 - 1); cy++)
            for (int cx = FloorDiv(box.X0); cx <= FloorDiv(box.X1 - 1); cx++)
            {
                long k = Key(cx, cy, cz);
                if (!index.TryGetValue(k, out var list)) index[k] = list = [];
                list.Add(b);
            }
        }
        ByChunk = index.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    private static int FloorDiv(int v) => v >> 5;

    /// <summary>Carves chunk (cx, cy, cz)'s boxes into its blocks (indexed as <see cref="ChunkData.Index"/>); returns how
    /// many boxes it carved.</summary>
    public int Carve(int cx, int cy, int cz, Span<BlockId> blocks)
    {
        if (!ByChunk.TryGetValue(Key(cx, cy, cz), out var mine)) return 0;
        int ox = cx * S, oy = cy * S, oz = cz * S;
        for (int pass = 0; pass < 2; pass++)
        foreach (int b in mine)
        {
            var box = Boxes[b];
            if (pass == 0 && box.Op == BoxOp.Air) continue;
            int x0 = Math.Max(box.X0, ox), x1 = Math.Min(box.X1, ox + S);
            int y0 = Math.Max(box.Y0, oy), y1 = Math.Min(box.Y1, oy + S);
            int z0 = Math.Max(box.Z0, oz), z1 = Math.Min(box.Z1, oz + S);
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            {
                bool slab = y == box.Y0 || y == box.Y1 - 1;
                bool shellZ = z == box.Z0 || z == box.Z1 - 1;
                int row = (y - oy) * S + (z - oz) * S * S - ox;
                if (pass == 1)
                {
                    // Air: everything inside a room's shell or between a hall's slabs, all of an air box.
                    if (box.Op == BoxOp.Air) { blocks.Slice(row + x0, x1 - x0).Fill(BlockId.Air); continue; }
                    if (slab || (box.Op == BoxOp.Room && shellZ)) continue;
                    int a = box.Op == BoxOp.Room ? Math.Max(x0, box.X0 + 1) : x0, e = box.Op == BoxOp.Room ? Math.Min(x1, box.X1 - 1) : x1;
                    if (e > a) blocks.Slice(row + a, e - a).Fill(BlockId.Air);
                }
                else if (slab || (box.Op == BoxOp.Room && shellZ))
                    blocks.Slice(row + x0, x1 - x0).Fill(BlockId.StoneBrick);
                else if (box.Op == BoxOp.Room)
                {
                    if (x0 == box.X0) blocks[row + x0] = BlockId.StoneBrick;
                    if (x1 == box.X1) blocks[row + x1 - 1] = BlockId.StoneBrick;
                }
            }
        }
        return mine.Length;
    }
}
