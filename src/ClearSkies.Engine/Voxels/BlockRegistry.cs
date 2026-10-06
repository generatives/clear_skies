using ClearSkies.Engine.ECS;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

public static class BlockRegistry
{
    private static readonly BlockDef[] Defs = new BlockDef[256];

    static BlockRegistry()
    {
        Register(new BlockDef { Id = BlockId.Air,   Name = "Air",   Color = default,                   IsSolid = false, PlaceOriented = false, LightEmission = 0,  Weight = 0 });
        Register(new BlockDef { Id = BlockId.Grass, Name = "Grass", Color = new(0.35f, 0.75f, 0.25f), IsSolid = true, PlaceOriented = false,  LightEmission = 0, Weight = 2,
            Texture = "dirt_grass", TextureTop = "leaves", TextureBottom = "dirt" });
        Register(new BlockDef { Id = BlockId.Dirt,  Name = "Dirt",  Color = new(0.55f, 0.38f, 0.22f), IsSolid = true, PlaceOriented = false,  LightEmission = 0, Weight = 2,
            Texture = "dirt" });
        // Deep/foundation fill (island core, dome underside, subsurface everywhere) — "greystone" is a
        // darker, more foundational-looking tile than "stone", which is reserved for mountain surfaces.
        Register(new BlockDef { Id = BlockId.Stone, Name = "Stone", Color = new(0.42f, 0.45f, 0.47f), IsSolid = true, PlaceOriented = false,  LightEmission = 0, Weight = 6,
            Texture = "greystone" });
        Register(new BlockDef { Id = BlockId.Lamp,  Name = "Lamp",  Color = new(1.00f, 0.95f, 0.80f), IsSolid = true, PlaceOriented = false,  LightEmission = 14, Weight = 3 });
        // Coloured lamps (ray-traced RGB light). Untextured: their flat Color shows what they emit.
        Register(new BlockDef { Id = BlockId.RedLamp,   Name = "Red Lamp",   Color = new(1.00f, 0.25f, 0.20f), IsSolid = true, PlaceOriented = false, LightEmission = 14, Weight = 3,
            LightColor = new(1.00f, 0.20f, 0.15f) });
        Register(new BlockDef { Id = BlockId.GreenLamp, Name = "Green Lamp", Color = new(0.30f, 1.00f, 0.35f), IsSolid = true, PlaceOriented = false, LightEmission = 14, Weight = 3,
            LightColor = new(0.20f, 1.00f, 0.25f) });
        Register(new BlockDef { Id = BlockId.BlueLamp,  Name = "Blue Lamp",  Color = new(0.30f, 0.45f, 1.00f), IsSolid = true, PlaceOriented = false, LightEmission = 14, Weight = 3,
            LightColor = new(0.20f, 0.35f, 1.00f) });
        Register(new BlockDef { Id = BlockId.Wood,  Name = "Wood",  Color = new(0.55f, 0.40f, 0.25f), IsSolid = true, PlaceOriented = false,  LightEmission = 0, Weight = 1,
            Texture = "wood" });

        // Milestone 5: minimal airship blocks. Fan applies thrust along its top's direction; Buoyant applies
        // constant passive lift. See AirshipFlightSystem. Fan's thrust-exit
        // face (its Top, which BlockDef orients to wherever the voxel's top points) gets a distinct
        // texture so you can see which way it'll push just by looking at it; every other face uses the
        // plain metal housing.
        // Fan and Buoyant are entity blocks (see BlockDef.Components): AirshipFlightSystem finds a ship's Fans and
        // Buoyant blocks through their entities instead of scanning voxels.
        Register(new BlockDef { Id = BlockId.Fan, Name = "Fan", Color = new(0.85f, 0.55f, 0.15f), IsSolid = true, PlaceOriented = true, LightEmission = 0, Weight = 3,
            OpaqueModel = true, // its housing fills the cell: it shades what's behind it and hides faces against it like a cube
            Model = "thruster/thruster.gltf", IconTexture = "thruster.png", Components = e => e.Set(new Fan()) });
        //Register(new BlockDef { Id = BlockId.Fan,     Name = "Fan",     Color = new(0.85f, 0.55f, 0.15f), IsSolid = true, LightEmission = 0, Weight = 3,
        //    Texture = "metal", TextureTop = "thruster" });
        // Model block (see BlockDef.Model): the Blockbench lever. It lets light through, since it only
        // fills a sliver of its cell, and Passable so characters walk through it. An interactive entity block: the player drags its arm (LeverControlSystem).
        Register(new BlockDef { Id = BlockId.Lever, Name = "Lever", Color = new(0.45f, 0.35f, 0.25f), IsSolid = true, PlaceOriented = true, Passable = true, LightEmission = 0, Weight = 1,
            Model = "lever/lever.gltf", IconTexture = "lever.png", Components = e => { e.Set(new Lever()); e.Set(new Interactive()); } });
        // The Blockbench ship's wheel, also Passable: the player grabs its rim and turns it to steer (SteeringWheelControlSystem).
        Register(new BlockDef { Id = BlockId.SteeringWheel, Name = "Steering Wheel", Color = new(0.50f, 0.36f, 0.22f), IsSolid = true, PlaceOriented = true, Passable = true, LightEmission = 0, Weight = 1,
            Model = "steering_wheel/steering_wheel.gltf", IconTexture = "steering_wheel.png", Components = e => { e.Set(new SteeringWheel()); e.Set(new Interactive()); } });
        Register(new BlockDef { Id = BlockId.Buoyant, Name = "Buoyant", Color = new(0.55f, 0.85f, 1.00f), IsSolid = true, LightEmission = 0, Weight = 1,
            Components = e => e.Set(new Buoyant()) });

        // Floating-island world-gen biome blocks.
        Register(new BlockDef { Id = BlockId.Sand,  Name = "Sand",  Color = new(0.66f, 0.50f, 0.32f), IsSolid = true, PlaceOriented = false, LightEmission = 0, Weight = 2,
            Texture = "dirt_sand", TextureTop = "sand", TextureBottom = "dirt" });
        // Water is Translucent (drawn alpha-blended, its opaque texture faded by Alpha) and lets light through. It's
        // Passable: raycasts still hit it (so it can be targeted, placed against and scooped up), but bodies sink
        // through it instead of standing on it.
        Register(new BlockDef { Id = BlockId.Water, Name = "Water", Color = new(0.20f, 0.45f, 0.85f), IsSolid = true, PlaceOriented = false, Passable = true, LightEmission = 0, Weight = 1,
            Texture = "water", Layer = RenderLayer.Translucent, Alpha = 0.6f });
        // Plain "snow" on every face: a thick snowpack should read as snow all the way round, not a
        // rock/snow blend on the sides (that blend texture is reserved for a thin single-layer cap —
        // the world generator gives Snow multiple layers of depth, so this is the common case).
        Register(new BlockDef { Id = BlockId.Snow,  Name = "Snow",  Color = new(0.95f, 0.97f, 1.00f), IsSolid = true, PlaceOriented = false, LightEmission = 0, Weight = 6,
            Texture = "snow" });
        // Mountain-surface bare rock, one shade lighter than the Stone foundation beneath it. ("rock" is
        // a decorative, mostly-transparent overlay sprite meant to be composited over another texture,
        // not a standalone block face, so it's not used here.)
        Register(new BlockDef { Id = BlockId.Rock,  Name = "Rock",  Color = new(0.52f, 0.52f, 0.55f), IsSolid = true, PlaceOriented = false, LightEmission = 0, Weight = 6,
            Texture = "stone" });

        // Glass: a solid, colliding cube that light passes through. Cut out like Minecraft's glass: its texture is
        // mostly clear, with a few streaks of glare, so it's drawn with the world minus its clear texels, no blending.
        Register(new BlockDef { Id = BlockId.Glass, Name = "Glass", Color = new(0.80f, 0.90f, 0.95f), IsSolid = true, PlaceOriented = false, LightEmission = 0, Weight = 2,
            Texture = "glass", Layer = RenderLayer.Cutout });

        // Plants and pebbles: crossed billboards (BlockShape.Cross) of one sprite each. Passable, so they're walked
        // through, but still targetable so they can be broken, and Replaceable: a block placed against one takes its
        // place. Colour is only a fallback (and the hotbar's swatch) if the sprite is missing.
        RegisterCross(BlockId.ShortGrass,    "Short Grass",    "grass1",         new(0.20f, 0.70f, 0.40f));
        RegisterCross(BlockId.GrassTuft,     "Grass Tuft",     "grass2",         new(0.20f, 0.70f, 0.40f));
        RegisterCross(BlockId.GrassBlades,   "Grass Blades",   "grass3",         new(0.20f, 0.70f, 0.40f));
        RegisterCross(BlockId.TallGrass,     "Tall Grass",     "grass4",         new(0.20f, 0.70f, 0.40f));
        RegisterCross(BlockId.DryGrass,      "Dry Grass",      "grass_tan",      new(0.85f, 0.78f, 0.60f));
        RegisterCross(BlockId.BrownGrass,    "Brown Grass",    "grass_brown",    new(0.62f, 0.40f, 0.20f));
        RegisterCross(BlockId.RedMushroom,   "Red Mushroom",   "mushroom_red",   new(0.95f, 0.40f, 0.15f));
        RegisterCross(BlockId.BrownMushroom, "Brown Mushroom", "mushroom_brown", new(0.55f, 0.35f, 0.20f));
        RegisterCross(BlockId.TanMushroom,   "Tan Mushroom",   "mushroom_tan",   new(0.85f, 0.75f, 0.60f));
        RegisterCross(BlockId.Pebbles,       "Pebbles",        "rock",           new(0.55f, 0.62f, 0.65f));
        RegisterCross(BlockId.MossyPebbles,  "Mossy Pebbles",  "rock_moss",      new(0.45f, 0.62f, 0.50f));

        // Trees and cacti, grown by world generation. Leaves are cut out like glass, so they show what's behind their
        // gaps; pine needles are the solid leaf texture, so pines read darker and denser.
        Register(new BlockDef { Id = BlockId.Log, Name = "Log", Color = new(0.45f, 0.32f, 0.20f), IsSolid = true, PlaceOriented = false, LightEmission = 0, Weight = 1,
            Texture = "trunk_side", TextureTop = "trunk_top", TextureBottom = "trunk_top" });
        Register(new BlockDef { Id = BlockId.Leaves, Name = "Leaves", Color = new(0.20f, 0.65f, 0.35f), IsSolid = true, PlaceOriented = false, LightEmission = 0, Weight = 0.2f,
            Texture = "leaves_transparent", Layer = RenderLayer.Cutout });
        Register(new BlockDef { Id = BlockId.PineLeaves, Name = "Pine Needles", Color = new(0.15f, 0.50f, 0.30f), IsSolid = true, PlaceOriented = false, LightEmission = 0, Weight = 0.2f,
            Texture = "leaves" });
        Register(new BlockDef { Id = BlockId.Cactus, Name = "Cactus", Color = new(0.25f, 0.65f, 0.35f), IsSolid = true, PlaceOriented = false, LightEmission = 0, Weight = 1,
            Texture = "cactus_side", TextureTop = "cactus_top", TextureBottom = "cactus_top" });
    }

    private static void RegisterCross(BlockId id, string name, string texture, Vector3D<float> color) =>
        Register(new BlockDef { Id = id, Name = name, Color = color, IsSolid = true, Passable = true, Weight = 0.1f,
            Shape = BlockShape.Cross, Texture = texture });

    private static void Register(BlockDef def) => Defs[(byte)def.Id] = def;

    public static ref readonly BlockDef Get(BlockId id) => ref Defs[(byte)id];

    /// <summary>Whether <paramref name="id"/> is a registered block (air included), e.g. to check one read from the
    /// network or a save.</summary>
    public static bool IsDefined(BlockId id) => id == BlockId.Air || Defs[(byte)id].Id == id && Defs[(byte)id].Name is not null;
}
