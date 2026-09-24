using ClearSkies.Engine.ECS;
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
        // Deep/foundation fill (island core, dome underside, subsurface everywhere) — "greystone" is a
        // darker, more foundational-looking tile than "stone", which is reserved for mountain surfaces.
        Register(new BlockDef { Id = BlockId.Stone, Name = "Stone", Color = new(0.42f, 0.45f, 0.47f), IsSolid = true,  LightEmission = 0,  Opacity = 15, Weight = 6,
            Texture = "greystone" });
        Register(new BlockDef { Id = BlockId.Lamp,  Name = "Lamp",  Color = new(1.00f, 0.95f, 0.80f), IsSolid = true,  LightEmission = 14, Opacity = 15, Weight = 3 });
        // Coloured lamps (ray-traced RGB light). Untextured: their flat Color shows what they emit.
        Register(new BlockDef { Id = BlockId.RedLamp,   Name = "Red Lamp",   Color = new(1.00f, 0.25f, 0.20f), IsSolid = true, LightEmission = 14, Opacity = 15, Weight = 3,
            LightColor = new(1.00f, 0.20f, 0.15f) });
        Register(new BlockDef { Id = BlockId.GreenLamp, Name = "Green Lamp", Color = new(0.30f, 1.00f, 0.35f), IsSolid = true, LightEmission = 14, Opacity = 15, Weight = 3,
            LightColor = new(0.20f, 1.00f, 0.25f) });
        Register(new BlockDef { Id = BlockId.BlueLamp,  Name = "Blue Lamp",  Color = new(0.30f, 0.45f, 1.00f), IsSolid = true, LightEmission = 14, Opacity = 15, Weight = 3,
            LightColor = new(0.20f, 0.35f, 1.00f) });
        Register(new BlockDef { Id = BlockId.Wood,  Name = "Wood",  Color = new(0.55f, 0.40f, 0.25f), IsSolid = true,  LightEmission = 0,  Opacity = 15, Weight = 1,
            Texture = "wood" });

        // Milestone 5: minimal airship blocks. Fan applies thrust along its top's direction; Buoyant applies
        // constant passive lift. See AirshipFlightSystem. Fan's thrust-exit
        // face (its Top, which BlockDef orients to wherever the voxel's top points) gets a distinct
        // texture so you can see which way it'll push just by looking at it; every other face uses the
        // plain metal housing.
        // Fan and Buoyant are entity blocks (see BlockDef.Components): AirshipFlightSystem finds a ship's Fans and
        // Buoyant blocks through their entities instead of scanning voxels.
        Register(new BlockDef { Id = BlockId.Fan, Name = "Fan", Color = new(0.85f, 0.55f, 0.15f), IsSolid = true, LightEmission = 0, Opacity = 0, Weight = 3,
            Model = "thruster/thruster.gltf", Components = e => e.Set(new Fan()) });
        //Register(new BlockDef { Id = BlockId.Fan,     Name = "Fan",     Color = new(0.85f, 0.55f, 0.15f), IsSolid = true, LightEmission = 0, Opacity = 15, Weight = 3,
        //    Texture = "metal", TextureTop = "thruster" });
        // Model block (see BlockDef.Model): the Blockbench lever. Transparent to light (Opacity 0) since it only
        // fills a sliver of its cell. An interactive entity block: the player drags its arm (LeverControlSystem).
        Register(new BlockDef { Id = BlockId.Lever, Name = "Lever", Color = new(0.45f, 0.35f, 0.25f), IsSolid = true, LightEmission = 0, Opacity = 0, Weight = 1,
            Model = "lever/lever.gltf", Components = e => { e.Set(new Lever()); e.Set(new Interactive()); } });
        // The Blockbench ship's wheel: the player grabs its rim and turns it to steer (SteeringWheelControlSystem).
        Register(new BlockDef { Id = BlockId.SteeringWheel, Name = "Steering Wheel", Color = new(0.50f, 0.36f, 0.22f), IsSolid = true, LightEmission = 0, Opacity = 0, Weight = 1,
            Model = "steering_wheel/steering_wheel.gltf", Components = e => { e.Set(new SteeringWheel()); e.Set(new Interactive()); } });
        Register(new BlockDef { Id = BlockId.Buoyant, Name = "Buoyant", Color = new(0.55f, 0.85f, 1.00f), IsSolid = true, LightEmission = 0, Opacity = 15, Weight = 1,
            Components = e => e.Set(new Buoyant()) });

        // Floating-island world-gen biome blocks. Water is opaque (no alpha-blend render pipeline
        // exists yet) — it renders, collides, and blocks movement like ordinary solid ground.
        Register(new BlockDef { Id = BlockId.Sand,  Name = "Sand",  Color = new(0.76f, 0.70f, 0.50f), IsSolid = true, LightEmission = 0, Opacity = 15, Weight = 2,
            Texture = "dirt_sand", TextureTop = "sand", TextureBottom = "dirt" });
        Register(new BlockDef { Id = BlockId.Water, Name = "Water", Color = new(0.20f, 0.45f, 0.85f), IsSolid = true, LightEmission = 0, Opacity = 15, Weight = 3,
            Texture = "water" });
        // Plain "snow" on every face: a thick snowpack should read as snow all the way round, not a
        // rock/snow blend on the sides (that blend texture is reserved for a thin single-layer cap —
        // SkyWorldGenerator now always gives Snow multiple layers of depth, so this is the common case).
        Register(new BlockDef { Id = BlockId.Snow,  Name = "Snow",  Color = new(0.95f, 0.97f, 1.00f), IsSolid = true, LightEmission = 0, Opacity = 15, Weight = 6,
            Texture = "snow" });
        // Mountain-surface bare rock, one shade lighter than the Stone foundation beneath it. ("rock" is
        // a decorative, mostly-transparent overlay sprite meant to be composited over another texture,
        // not a standalone block face — with no alpha-blend pipeline it shows its dark "empty" fill
        // instead, so it's not used here.)
        Register(new BlockDef { Id = BlockId.Rock,  Name = "Rock",  Color = new(0.52f, 0.52f, 0.55f), IsSolid = true, LightEmission = 0, Opacity = 15, Weight = 6,
            Texture = "stone" });
    }

    private static void Register(BlockDef def) => Defs[(byte)def.Id] = def;

    public static ref readonly BlockDef Get(BlockId id) => ref Defs[(byte)id];
}
