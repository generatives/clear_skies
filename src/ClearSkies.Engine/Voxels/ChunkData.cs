namespace ClearSkies.Engine.Voxels;

public sealed class ChunkData
{
    public const int Size = 32;

    private readonly BlockId[] _blocks = new BlockId[Size * Size * Size];

    public bool IsDirty { get; set; }

    public BlockId Get(int x, int y, int z) => _blocks[Index(x, y, z)];

    public void Set(int x, int y, int z, BlockId id)
    {
        _blocks[Index(x, y, z)] = id;
        IsDirty = true;
    }

    public static int Index(int x, int y, int z) => x + Size * (y + Size * z);

    public bool HasAnySolid()
    {
        for (int i = 0; i < _blocks.Length; i++)
            if (_blocks[i] != BlockId.Air) return true;
        return false;
    }
}
