using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

public static class BlockRegistry
{
    private static readonly BlockDef[] Defs = new BlockDef[256];

    static BlockRegistry()
    {
        Register(new BlockDef { Id = BlockId.Air,   Name = "Air",   Color = default,                   IsSolid = false });
        Register(new BlockDef { Id = BlockId.Grass, Name = "Grass", Color = new(0.35f, 0.75f, 0.25f), IsSolid = true  });
        Register(new BlockDef { Id = BlockId.Dirt,  Name = "Dirt",  Color = new(0.55f, 0.38f, 0.22f), IsSolid = true  });
        Register(new BlockDef { Id = BlockId.Stone, Name = "Stone", Color = new(0.52f, 0.52f, 0.55f), IsSolid = true  });
    }

    private static void Register(BlockDef def) => Defs[(byte)def.Id] = def;

    public static ref readonly BlockDef Get(BlockId id) => ref Defs[(byte)id];
}
