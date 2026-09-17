using System;
using System.Runtime.InteropServices;

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

    /// <summary>Zero-copy raw byte view of the block array, for bulk serialization.</summary>
    internal ReadOnlySpan<byte> AsBytes() => MemoryMarshal.Cast<BlockId, byte>(_blocks);

    /// <summary>Overwrites every block from a raw byte buffer previously produced by <see cref="AsBytes"/>.
    /// Does not touch <see cref="IsDirty"/> — the caller decides what that should be afterward.</summary>
    internal void LoadBytes(ReadOnlySpan<byte> bytes)
    {
        var dst = MemoryMarshal.Cast<BlockId, byte>(_blocks);
        if (bytes.Length != dst.Length)
            throw new ArgumentException($"Expected {dst.Length} bytes, got {bytes.Length}.", nameof(bytes));
        bytes.CopyTo(dst);
    }
}
