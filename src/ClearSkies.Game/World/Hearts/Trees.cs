using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Trees and cacti on <see cref="ContinentTerrain"/>'s tops, kept simple, Minecraft style: round-topped oaks on grass
/// across the middle heights, giving way to pines higher up (they go on up into the snow), and cacti on sand.
///
/// Whether a column grows one is picked by a hash of its top, so the same in every chunk that generates it. A tree is
/// only grown at least <see cref="Reach"/> blocks in from a chunk column's sides, so all of it lands in the chunk
/// column that decides it.
/// </summary>
public static class Trees
{
    public enum Kind : byte { None, Oak, Pine, Cactus }

    /// <summary>How far a tree reaches out from its trunk, at most.</summary>
    public const int Reach = 3;

    /// <summary>How far a tree reaches up from the top it grows on, at most.</summary>
    public const int MaxHeight = 11;

    // Oaks fade out and pines in over these heights (world Y of the top).
    private const float PineStart = 380f, PineFull = 520f;

    /// <summary>What grows from the open cell above (x, top, z), whose block is <paramref name="ground"/> (from
    /// <see cref="ContinentTerrain.Block"/>): a tree, a cactus or nothing. Trees grow thickest where
    /// <paramref name="patch"/> (from <see cref="ContinentTerrain.Patch"/>) is high, in the same meadows as the
    /// grass.</summary>
    public static Kind At(BlockId ground, int x, int top, int z, float patch, ulong seed)
    {
        ulong h = Hash(x, top, z, seed);
        float r = (h & 0xFFFFFF) / 16777216f;
        float pick = ((h >> 24) & 0xFFFF) / 65536f;
        switch (ground)
        {
            case BlockId.Grass:
            {
                if (top < ContinentTerrain.DryLine) return Kind.None;
                if (r >= 0.002f + 0.015f * Smoothstep(0.45f, 0.85f, patch)) return Kind.None;
                return pick < Smoothstep(PineStart, PineFull, top) ? Kind.Pine : Kind.Oak;
            }
            case BlockId.Snow:
                return r < 0.006f ? Kind.Pine : Kind.None;
            case BlockId.Sand:
                return r < 0.004f ? Kind.Cactus : Kind.None;
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
