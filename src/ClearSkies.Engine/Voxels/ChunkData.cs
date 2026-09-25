using System;
using System.Runtime.InteropServices;

namespace ClearSkies.Engine.Voxels;

public sealed class ChunkData
{
    public const int Size = 32;
    public const int Shift = 5; // log2(Size)

    private readonly BlockId[] _blocks  = new BlockId[Size * Size * Size];
    private readonly BlockOrientation[] _orientations = new BlockOrientation[Size * Size * Size];

    public bool IsDirty { get; set; }

    public BlockId Get(int x, int y, int z) => _blocks[Index(x, y, z)];
    public BlockOrientation GetOrientation(int x, int y, int z) => _orientations[Index(x, y, z)];

    public void Set(int x, int y, int z, BlockId id) => Set(x, y, z, id, BlockOrientation.Upright);

    public void Set(int x, int y, int z, BlockId id, BlockOrientation orientation)
    {
        int i = Index(x, y, z);
        _blocks[i]       = id;
        _orientations[i] = orientation;
        IsDirty = true;
    }

    public static int Index(int x, int y, int z) => x + Size * (y + Size * z);

    public bool HasAnySolid()
    {
        for (int i = 0; i < _blocks.Length; i++)
            if (_blocks[i] != BlockId.Air) return true;
        return false;
    }

    /// <summary>Zero-copy raw byte views of the block/orientation arrays, for bulk serialization (an orientation is
    /// its <see cref="BlockOrientation.ToByte"/>).</summary>
    internal ReadOnlySpan<byte> BlocksAsBytes()       => MemoryMarshal.Cast<BlockId, byte>(_blocks);
    internal ReadOnlySpan<byte> OrientationsAsBytes() => MemoryMarshal.Cast<BlockOrientation, byte>(_orientations);

    /// <summary>Overwrites every block/orientation from a raw byte buffer previously produced by the
    /// matching <c>*AsBytes</c> method. Does not touch <see cref="IsDirty"/> — the caller decides
    /// what that should be afterward.</summary>
    internal void LoadBlockBytes(ReadOnlySpan<byte> bytes)
    {
        var dst = MemoryMarshal.Cast<BlockId, byte>(_blocks);
        if (bytes.Length != dst.Length)
            throw new ArgumentException($"Expected {dst.Length} bytes, got {bytes.Length}.", nameof(bytes));
        bytes.CopyTo(dst);
    }

    internal void LoadOrientationBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != _orientations.Length)
            throw new ArgumentException($"Expected {_orientations.Length} bytes, got {bytes.Length}.", nameof(bytes));
        for (int i = 0; i < bytes.Length; i++)
            _orientations[i] = BlockOrientation.FromByte(bytes[i]); // validated: a bad byte would index out of range
    }
}
