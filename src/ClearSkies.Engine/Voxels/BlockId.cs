namespace ClearSkies.Engine.Voxels;

public enum BlockId : byte
{
    Air     = 0,
    Grass   = 1,
    Dirt    = 2,
    Stone   = 3,
    Lamp    = 4,
    Wood    = 5,
    Fan     = 6,
    Buoyant = 7,
    Sand    = 8,
    Water   = 9,
    Snow    = 10,
    Rock    = 11,
    RedLamp   = 12,
    GreenLamp = 13,
    BlueLamp  = 14,
    Lever     = 15,
    SteeringWheel = 16,
    Glass         = 17,

    // Plants and pebbles: crossed billboards (BlockShape.Cross).
    ShortGrass    = 18,
    GrassTuft     = 19,
    GrassBlades   = 20,
    TallGrass     = 21,
    DryGrass      = 22,
    BrownGrass    = 23,
    RedMushroom   = 24,
    BrownMushroom = 25,
    TanMushroom   = 26,
    Pebbles       = 27,
    MossyPebbles  = 28,

    // Trees and cacti.
    Log           = 29,
    Leaves        = 30,
    PineLeaves    = 31,
    Cactus        = 32,
}
