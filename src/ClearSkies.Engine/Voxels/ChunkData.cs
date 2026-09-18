using System;
using System.Runtime.InteropServices;

namespace ClearSkies.Engine.Voxels;

public sealed class ChunkData
{
    public const int Size = 32;

    private readonly BlockId[] _blocks  = new BlockId[Size * Size * Size];
    private readonly Facing[]  _facings = new Facing[Size * Size * Size];

    public bool IsDirty { get; set; }

    public BlockId Get(int x, int y, int z) => _blocks[Index(x, y, z)];
    public Facing  GetFacing(int x, int y, int z) => _facings[Index(x, y, z)];

    public void Set(int x, int y, int z, BlockId id, Facing facing = Facing.Up)
    {
        int i = Index(x, y, z);
        _blocks[i]  = id;
        _facings[i] = facing;
        IsDirty = true;
    }

    public static int Index(int x, int y, int z) => x + Size * (y + Size * z);

    public bool HasAnySolid()
    {
        for (int i = 0; i < _blocks.Length; i++)
            if (_blocks[i] != BlockId.Air) return true;
        return false;
    }

    /// <summary>Zero-copy raw byte views of the block/facing arrays, for bulk serialization.</summary>
    internal ReadOnlySpan<byte> BlocksAsBytes()  => MemoryMarshal.Cast<BlockId, byte>(_blocks);
    internal ReadOnlySpan<byte> FacingsAsBytes() => MemoryMarshal.Cast<Facing, byte>(_facings);

    /// <summary>Overwrites every block/facing from a raw byte buffer previously produced by the
    /// matching <c>*AsBytes</c> method. Does not touch <see cref="IsDirty"/> — the caller decides
    /// what that should be afterward.</summary>
    internal void LoadBlockBytes(ReadOnlySpan<byte> bytes)
    {
        var dst = MemoryMarshal.Cast<BlockId, byte>(_blocks);
        if (bytes.Length != dst.Length)
            throw new ArgumentException($"Expected {dst.Length} bytes, got {bytes.Length}.", nameof(bytes));
        bytes.CopyTo(dst);
    }

    internal void LoadFacingBytes(ReadOnlySpan<byte> bytes)
    {
        var dst = MemoryMarshal.Cast<Facing, byte>(_facings);
        if (bytes.Length != dst.Length)
            throw new ArgumentException($"Expected {dst.Length} bytes, got {bytes.Length}.", nameof(bytes));
        bytes.CopyTo(dst);
    }
}
