namespace ClearSkies.Engine.Voxels;

/// <summary>
/// Per-voxel light storage for one 32³ chunk. Each byte packs sky light (high nibble, 0-15) and
/// block light (low nibble, 0-15). Coordinates match <see cref="ChunkData"/> (same index formula).
/// </summary>
public sealed class LightData
{
    private readonly byte[] _data = new byte[ChunkData.Size * ChunkData.Size * ChunkData.Size];

    public byte GetSky(int x, int y, int z)   => (byte)(_data[ChunkData.Index(x, y, z)] >> 4);
    public byte GetBlock(int x, int y, int z) => (byte)(_data[ChunkData.Index(x, y, z)] & 0x0F);

    public void SetSky(int x, int y, int z, byte v)
    {
        int i = ChunkData.Index(x, y, z);
        _data[i] = (byte)((_data[i] & 0x0F) | ((v & 0xF) << 4));
    }

    public void SetBlock(int x, int y, int z, byte v)
    {
        int i = ChunkData.Index(x, y, z);
        _data[i] = (byte)((_data[i] & 0xF0) | (v & 0xF));
    }

    public byte GetRaw(int x, int y, int z) => _data[ChunkData.Index(x, y, z)];

    public void Fill(byte sky, byte block)
        => Array.Fill(_data, (byte)((sky << 4) | (block & 0xF)));
}
