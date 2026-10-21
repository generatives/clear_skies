using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Trees and cacti on <see cref="ContinentTerrain"/>'s tops, kept simple, Minecraft style: round-topped oaks on grass
/// across the middle heights (birches with orange leaves in autumn woods), giving way to pines higher up (they go on up
/// into the snow), and cacti on sand, sparser on red sand.
///
/// Whether a column grows one is picked by a hash of its top, so the same in every chunk that generates it. A tree is
/// only grown at least its <see cref="ReachOf"/> blocks in from a chunk column's sides, so all of it lands in the chunk
/// column that decides it.
/// </summary>
public static class Trees
{
    public enum Kind : byte { None, Oak, Pine, Cactus, Birch, Jungle }

    /// <summary>How far a tree reaches out from its trunk, at most, but for jungle trees (see <see cref="ReachOf"/>).</summary>
    public const int Reach = 3;

    /// <summary>How far any tree reaches up from the top it grows on, at most (see <see cref="HeightOf"/>).</summary>
    public const int MaxHeight = 17;

    private const int JungleReach = 5;

    /// <summary>How far a <paramref name="kind"/> reaches out from its trunk, at most.</summary>
    public static int ReachOf(Kind kind) => kind == Kind.Jungle ? JungleReach : Reach;

    /// <summary>How far a <paramref name="kind"/> reaches up from the top it grows on, at most.</summary>
    public static int HeightOf(Kind kind) => kind == Kind.Jungle ? MaxHeight : 11;

    // Oaks fade out and pines in over these heights (world Y of the top).
    private const float PineStart = 680f, PineFull = 820f;

    /// <summary>What grows from the open cell above (x, top, z), whose block is <paramref name="ground"/> (from
    /// <see cref="ContinentTerrain.Block"/>): a tree, a cactus or nothing. They grow where <paramref name="woods"/>
    /// (from <see cref="ContinentTerrain.Woods"/>) is high, so they thin from woods to scattered trees, with the odd one
    /// standing alone in the open.</summary>
    public static Kind At(BlockId ground, int x, int top, int z, float woods, ulong seed, Biome biome = Biome.Temperate)
    {
        ulong h = Hash(x, top, z, seed);
        float r = (h & 0xFFFFFF) / 16777216f;
        float pick = ((h >> 24) & 0xFFFF) / 65536f;
        switch (ground)
        {
            case BlockId.Grass:
            {
                if (top < ContinentTerrain.DryLine && biome != Biome.Jungle) return Kind.None;
                switch (biome)
                {
                    case Biome.Jungle: return r < 0.002f + 0.035f * Grove(woods) ? Kind.Jungle : Kind.None;
                    case Biome.Tundra: return r < 0.0005f + 0.012f * Grove(woods) ? Kind.Pine : Kind.None;
                    case Biome.Steppe: return r < 0.0003f ? Kind.Pine : Kind.None;
                }
                if (r >= 0.001f + 0.03f * Grove(woods)) return Kind.None;
                return pick < Smoothstep(PineStart, PineFull, top) ? Kind.Pine : biome == Biome.Autumn ? Kind.Birch : Kind.Oak;
            }
            case BlockId.Snow:
                return r < 0.001f + 0.02f * Grove(woods) ? Kind.Pine : Kind.None;
            case BlockId.SnowyDirt:
                return r < 0.0005f + 0.008f * Grove(woods) ? Kind.Pine : Kind.None;
            case BlockId.Sand:
                return r < 0.001f + 0.012f * Grove(woods) ? Kind.Cactus : Kind.None;
            case BlockId.RedSand:
                return r < 0.0005f + 0.004f * Grove(woods) ? Kind.Cactus : Kind.None;
            default:
                return Kind.None;
        }
    }

    /// <summary>Grows a <paramref name="kind"/> on (x, top, z) (x and z within the chunk, at least <see cref="Reach"/>
    /// in from its sides; top in world Y): sets whatever of it falls in <paramref name="data"/>, whose bottom is at world
    /// Y <paramref name="originY"/>, over air and plants only.</summary>
    public static void Grow(ChunkData data, int originY, Kind kind, int x, int top, int z, ulong h)
    {
        int y0 = top + 1;
        switch (kind)
        {
            case Kind.Oak:
            {
                // A trunk 4-6 tall; two wide layers of leaves round its top, then two narrow, the top one a plus. Some
                // corners are left off.
                int height = 4 + (int)((h >> 48) % 3);
                int crown = y0 + height - 1;
                for (int y = y0; y <= crown; y++) Set(data, originY, x, y, z, BlockId.Log);
                int bit = 0;
                for (int y = crown - 2; y <= crown + 1; y++)
                {
                    int reach = y < crown ? 2 : 1;
                    for (int dz = -reach; dz <= reach; dz++)
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        bool corner = Math.Abs(dx) == reach && Math.Abs(dz) == reach;
                        if (corner && (y > crown || Bit(h, bit++))) continue;
                        Set(data, originY, x + dx, y, z + dz, BlockId.Leaves);
                    }
                }
                break;
            }
            case Kind.Birch:
            {
                // Tall and slender: a bare trunk 7-9 tall, then a narrow column of leaves up its top half, a plus
                // round the trunk with a clump bulging out to one side here and there, ending in a point.
                int height = 7 + (int)((h >> 48) % 3);
                int crown = y0 + height - 1;
                for (int y = y0; y <= crown; y++) Set(data, originY, x, y, z, BlockId.BirchLog);
                int bit = 0;
                for (int y = crown - height / 2; y <= crown + 1; y++)
                {
                    for (int d = 0; d < 4; d++)
                    {
                        int dx = d == 0 ? 1 : d == 1 ? -1 : 0, dz = d == 2 ? 1 : d == 3 ? -1 : 0;
                        if (y <= crown) Set(data, originY, x + dx, y, z + dz, BlockId.AutumnLeaves);
                        // A clump: the plus's arm reaching a block further, with its corners filled.
                        if (y < crown && Bit(h, bit++) && Bit(h, bit++))
                        {
                            Set(data, originY, x + 2 * dx, y, z + 2 * dz, BlockId.AutumnLeaves);
                            Set(data, originY, x + dx + dz, y, z + dz + dx, BlockId.AutumnLeaves);
                            Set(data, originY, x + dx - dz, y, z + dz - dx, BlockId.AutumnLeaves);
                        }
                    }
                }
                Set(data, originY, x, crown + 1, z, BlockId.AutumnLeaves);
                Set(data, originY, x, crown + 2, z, BlockId.AutumnLeaves);
                break;
            }
            case Kind.Jungle:
            {
                // A giant: a trunk two blocks thick, 12-15 tall, on buttress roots; two or three branches off its middle
                // half, each ending in a pad of leaves; a broad ragged dome of leaves on top, with leaves hanging off its
                // rim like vines. The trunk's (x, z) is its low corner, so the tree is centred half a block past it.
                int height = 12 + (int)(Rand(h, 0) % 4);
                int crown = y0 + height - 1;
                for (int y = y0; y <= crown; y++)
                    for (int t = 0; t < 4; t++) Set(data, originY, x + (t & 1), y, z + (t >> 1), BlockId.Log);

                // Roots: one block out from each face of the trunk, 1-3 tall, and now and then a second one out.
                for (int side = 0; side < 8; side++)
                {
                    var (rx, rz) = side switch
                    {
                        0 => (-1, 0), 1 => (-1, 1), 2 => (2, 0), 3 => (2, 1),
                        4 => (0, -1), 5 => (1, -1), 6 => (0, 2), _ => (1, 2),
                    };
                    ulong r = Rand(h, 1 + side);
                    if (r % 3 == 0) continue;
                    int up = 1 + (int)(r / 3 % 3);
                    for (int y = y0; y < y0 + up; y++) Set(data, originY, x + rx, y, z + rz, BlockId.Log);
                    if (r / 9 % 4 == 0) Set(data, originY, x + rx + Math.Sign(rx - 0.5f) * (rx is -1 or 2 ? 1 : 0), y0,
                                            z + rz + Math.Sign(rz - 0.5f) * (rz is -1 or 2 ? 1 : 0), BlockId.Log);
                }

                // Branches: out from the trunk and up a block, a leaf pad at the end.
                int branches = 2 + (int)(Rand(h, 10) % 2);
                for (int b = 0; b < branches; b++)
                {
                    ulong r = Rand(h, 11 + b);
                    int dir = (int)((r + (ulong)b * 2) % 4);
                    int dx = dir == 0 ? 1 : dir == 1 ? -1 : 0, dz = dir == 2 ? 1 : dir == 3 ? -1 : 0;
                    int by = y0 + height / 3 + (int)(r / 4 % (ulong)(height / 3));
                    int bx = dx > 0 ? x + 1 : x, bz = dz > 0 ? z + 1 : z; // the trunk face it leaves from
                    Set(data, originY, bx + dx, by, bz + dz, BlockId.Log);
                    Set(data, originY, bx + 2 * dx, by + 1, bz + 2 * dz, BlockId.Log);
                    int px = bx + 2 * dx, py = by + 2, pz = bz + 2 * dz;
                    for (int ly = py - 1; ly <= py; ly++)
                    for (int ox = -2; ox <= 2; ox++)
                    for (int oz = -2; oz <= 2; oz++)
                    {
                        int d2 = ox * ox + oz * oz;
                        if (d2 > (ly < py ? 4 : 2) || (d2 == 4 && Rand(r, ox * 5 + oz) % 2 == 0)) continue;
                        Set(data, originY, px + ox, ly, pz + oz, BlockId.Leaves);
                    }
                }

                // The canopy: discs round the trunk's centre, widest at its base, ragged at the rim.
                float cx = x + 0.5f, cz = z + 0.5f;
                for (int y = crown - 1; y <= crown + 2; y++)
                {
                    float radius = y <= crown ? 4.6f : y == crown + 1 ? 3.6f : 2.2f;
                    for (int lz = z - JungleReach + 1; lz <= z + JungleReach; lz++)
                    for (int lx = x - JungleReach + 1; lx <= x + JungleReach; lx++)
                    {
                        float d = MathF.Sqrt((lx - cx) * (lx - cx) + (lz - cz) * (lz - cz));
                        if (d > radius) continue;
                        ulong r = Rand(h, 20 + lx - x + 16 * (lz - z) + 256 * (y - crown));
                        if (d > radius - 1.2f && r % 3 == 0) continue; // a ragged rim
                        Set(data, originY, lx, y, lz, BlockId.Leaves);
                        // Vines: leaves hanging 1-3 blocks off the underside of the rim.
                        if (y == crown - 1 && d > radius - 1.2f && r / 3 % 4 == 0)
                            for (int drop = 1; drop <= 1 + (int)(r / 12 % 3); drop++) Set(data, originY, lx, y - drop, lz, BlockId.Leaves);
                    }
                }
                break;
            }
            case Kind.Pine:
            {
                // A trunk 7-10 tall in a cone of needles: rings widening and narrowing again on the way down, from a
                // point above the top to Reach at the bottom, a couple of blocks off the ground.
                int height = 7 + (int)((h >> 48) % 4);
                int crown = y0 + height - 1;
                for (int y = y0; y <= crown; y++) Set(data, originY, x, y, z, BlockId.Log);
                Set(data, originY, x, crown + 1, z, BlockId.PineLeaves);
                for (int y = crown; y >= y0 + 2; y--)
                {
                    int d = crown - y;
                    int reach = d == 0 ? 1 : Math.Min(Reach, d % 2 == 1 ? 1 + d / 3 : d / 3);
                    if (reach == 0) continue;
                    float within = reach * reach + 0.5f * reach;
                    for (int dz = -reach; dz <= reach; dz++)
                    for (int dx = -reach; dx <= reach; dx++)
                        if (dx * dx + dz * dz <= within) Set(data, originY, x + dx, y, z + dz, BlockId.PineLeaves);
                }
                break;
            }
            case Kind.Cactus:
            {
                int height = 1 + (int)((h >> 48) % 3);
                for (int y = y0; y < y0 + height; y++) Set(data, originY, x, y, z, BlockId.Cactus);
                break;
            }
        }
    }

    /// <summary>The hash <see cref="At"/> picks with, also passed to <see cref="Grow"/> to shape what grows.</summary>
    public static ulong Hash(int x, int top, int z, ulong seed)
    {
        ulong h = (ulong)(uint)x * 0xD1B54A32D192ED03UL ^ (ulong)(uint)top * 0xAEF17502108EF2D9UL
                ^ (ulong)(uint)z * 0x9E3779B97F4A7C15UL ^ (seed + 0x7EE5) * 0xD6E8FEB86659FD93UL;
        h ^= h >> 32; h *= 0xD6E8FEB86659FD93UL; h ^= h >> 32;
        return h;
    }

    /// <summary>0-1: how far into a wood a column is, by its <see cref="ContinentTerrain.Woods"/>.</summary>
    private static float Grove(float woods) => Smoothstep(0.5f, 0.85f, woods);

    /// <summary>A fresh random number from a tree's hash, the i-th of as many as it needs.</summary>
    private static ulong Rand(ulong h, int i)
    {
        ulong r = h + (ulong)(uint)i * 0x9E3779B97F4A7C15UL;
        r ^= r >> 30; r *= 0xBF58476D1CE4E5B9UL; r ^= r >> 27; r *= 0x94D049BB133111EBUL; r ^= r >> 31;
        return r;
    }

    private static bool Bit(ulong h, int i) => ((h >> (24 + i % 24)) & 1) != 0;

    private static void Set(ChunkData data, int originY, int x, int y, int z, BlockId block)
    {
        int ly = y - originY;
        if ((uint)ly >= ChunkData.Size) return;
        var there = data.Get(x, ly, z);
        if (there == BlockId.Air || BlockRegistry.Get(there).Replaceable) data.Set(x, ly, z, block);
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
