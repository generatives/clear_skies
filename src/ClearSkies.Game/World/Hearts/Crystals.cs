using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Glowing crystals in the caves: set into floors under other land and into ceilings over open gaps, in small clusters
/// so a cave has a few lit spots rather than an even sprinkle. Each crystal is a lamp, so they are kept sparse.
///
/// The world is cut into <see cref="Cell"/>-block cells (and <see cref="Band"/>-block bands of height, so caves above
/// each other get their own); one in <see cref="ClusterOdds"/> holds a cluster around a hashed spot, and blocks within
/// <see cref="Radius"/> of it become crystals at a chance falling off from its middle.
/// </summary>
public static class Crystals
{
    public const int Cell = 32, Band = 64, Radius = 3;
    private const int ClusterOdds = 5;

    /// <summary>Whether the cave floor or ceiling block at (x, y, z) is a crystal.</summary>
    public static bool At(int x, int y, int z, ulong seed)
    {
        int cx = FloorDiv(x, Cell), cz = FloorDiv(z, Cell), cy = FloorDiv(y, Band);
        ulong h = Trees.Hash(cx, cy, cz, seed ^ 0xC2757A15UL);
        if (h % ClusterOdds != 0) return false;
        int mx = cx * Cell + Radius + (int)((h >> 8) % (Cell - 2 * Radius));
        int mz = cz * Cell + Radius + (int)((h >> 24) % (Cell - 2 * Radius));
        int d2 = (x - mx) * (x - mx) + (z - mz) * (z - mz);
        if (d2 > Radius * Radius) return false;
        float chance = 0.6f * (1f - d2 / (float)(Radius * Radius + 1));
        return (Trees.Hash(x, y, z, seed ^ 0x9F1A3B5DUL) & 0xFFFF) < chance * 0x10000;
    }

    /// <summary>Red crystals in hot biomes, gold ones elsewhere (not blue: from afar it reads as holes to the sky).</summary>
    public static BlockId For(Biome biome) =>
        biome is Biome.Desert or Biome.Badlands ? BlockId.AmberCrystal : BlockId.GoldCrystal;

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
