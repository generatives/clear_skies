using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

public static class BlockRegistry
{
    private static readonly BlockDef[] Defs = new BlockDef[256];

    static BlockRegistry()
    {
        Register(new BlockDef { Id = BlockId.Air,   Name = "Air",   Color = default,                   IsSolid = false, LightEmission = 0,  Opacity = 0,  Weight = 0 });
        Register(new BlockDef { Id = BlockId.Grass, Name = "Grass", Color = new(0.35f, 0.75f, 0.25f), IsSolid = true,  LightEmission = 0,  Opacity = 15, Weight = 2,
            Texture = "dirt_grass", TextureTop = "grass_top", TextureBottom = "dirt" });
        Register(new BlockDef { Id = BlockId.Dirt,  Name = "Dirt",  Color = new(0.55f, 0.38f, 0.22f), IsSolid = true,  LightEmission = 0,  Opacity = 15, Weight = 2,
            Texture = "dirt" });
        Register(new BlockDef { Id = BlockId.Stone, Name = "Stone", Color = new(0.52f, 0.52f, 0.55f), IsSolid = true,  LightEmission = 0,  Opacity = 15, Weight = 6,
            Texture = "stone" });
        Register(new BlockDef { Id = BlockId.Lamp,  Name = "Lamp",  Color = new(1.00f, 0.95f, 0.80f), IsSolid = true,  LightEmission = 14, Opacity = 15, Weight = 3 });
        Register(new BlockDef { Id = BlockId.Wood,  Name = "Wood",  Color = new(0.55f, 0.40f, 0.25f), IsSolid = true,  LightEmission = 0,  Opacity = 15, Weight = 1,
            Texture = "wood" });

        // Milestone 5: minimal airship blocks. Fan applies thrust along its Facing; Buoyant applies
        // constant passive lift. See AirshipFlightSystem. Fan's thrust-exit
        // face (its Top, which BlockDef orients to wherever the voxel's Facing points) gets a distinct
        // texture so you can see which way it'll push just by looking at it; every other face uses the
        // plain metal housing.
        Register(new BlockDef { Id = BlockId.Fan,     Name = "Fan",     Color = new(0.85f, 0.55f, 0.15f), IsSolid = true, LightEmission = 0, Opacity = 15, Weight = 3,
            Texture = "metal", TextureTop = "thruster" });
        Register(new BlockDef { Id = BlockId.Buoyant, Name = "Buoyant", Color = new(0.55f, 0.85f, 1.00f), IsSolid = true, LightEmission = 0, Opacity = 15, Weight = 1 });

        // Floating-island world-gen biome blocks. Water is opaque (no alpha-blend render pipeline
        // exists yet) — it renders, collides, and blocks movement like ordinary solid ground.
        Register(new BlockDef { Id = BlockId.Sand,  Name = "Sand",  Color = new(0.76f, 0.70f, 0.50f), IsSolid = true, LightEmission = 0, Opacity = 15, Weight = 2,
            Texture = "dirt_sand", TextureTop = "sand", TextureBottom = "dirt" });
        Register(new BlockDef { Id = BlockId.Water, Name = "Water", Color = new(0.20f, 0.45f, 0.85f), IsSolid = true, LightEmission = 0, Opacity = 15, Weight = 3,
            Texture = "water" });
        Register(new BlockDef { Id = BlockId.Snow,  Name = "Snow",  Color = new(0.95f, 0.97f, 1.00f), IsSolid = true, LightEmission = 0, Opacity = 15, Weight = 6,
            Texture = "stone_snow", TextureTop = "snow", TextureBottom = "stone" });
        Register(new BlockDef { Id = BlockId.Rock,  Name = "Rock",  Color = new(0.45f, 0.43f, 0.40f), IsSolid = true, LightEmission = 0, Opacity = 15, Weight = 6,
            Texture = "rock" });
    }

    private static void Register(BlockDef def) => Defs[(byte)def.Id] = def;

    public static ref readonly BlockDef Get(BlockId id) => ref Defs[(byte)id];
}
