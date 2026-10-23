namespace ClearSkies.Engine.Voxels;

/// <summary>
/// A water block's state, kept in its orientation byte (water looks the same whichever way up it is, so it has no use
/// for one): its level, 0 for a source (water that stays put: lakes, rivers, placed water) or 1-7 for water flowing
/// from one, each a step further from it and an eighth lower; and whether it's falling (flowing water with water over
/// it, full height). <see cref="BlockOrientation.Upright"/>, which every water block had before water flowed and which
/// generation still places, reads as a source. See <see cref="ECS.WaterFlow"/>.
/// </summary>
public static class WaterLevel
{
    /// <summary>The weakest flowing water: it spreads no further.</summary>
    public const int Weakest = 7;

    private const int FallingBit = 8;

    // Upright's byte is a source, and the 16 states run on from it (wrapping round the 24 orientations).
    private static readonly int Base = BlockOrientation.Upright.ToByte();

    /// <summary>Source water.</summary>
    public static readonly BlockOrientation Source = BlockOrientation.Upright;

    /// <summary>Flowing water at <paramref name="level"/> (1-7), or falling.</summary>
    public static BlockOrientation Flowing(int level, bool falling = false)
        => BlockOrientation.FromByte((byte)((Base + level + (falling ? FallingBit : 0)) % BlockOrientation.Count));

    private static int Raw(BlockOrientation state) => (state.ToByte() - Base + BlockOrientation.Count) % BlockOrientation.Count;

    /// <summary>0 for a source or falling water, else 1-7.</summary>
    public static int Level(BlockOrientation state) => Raw(state) & 7;

    public static bool IsFalling(BlockOrientation state) => (Raw(state) & FallingBit) != 0;

    public static bool IsSource(BlockOrientation state) => Raw(state) == 0;

    /// <summary>The level water spreads from: a source and falling water spread as level 0.</summary>
    public static int SpreadLevel(BlockOrientation state) => IsFalling(state) ? 0 : Level(state);

    /// <summary>How high its surface stands, in eighths of the block: 8 for a source or falling water, 8 - level for
    /// flowing water.</summary>
    public static int Height(BlockOrientation state) => 8 - SpreadLevel(state);
}
