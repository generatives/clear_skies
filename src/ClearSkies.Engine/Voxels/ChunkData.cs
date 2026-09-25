using System;
using System.Runtime.InteropServices;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// One chunk's blocks and their orientations. A chunk that is all one block (sky, or the stone deep inside an island)
/// keeps just that block and allocates its arrays only once something different is set in it; see
/// <see cref="Compact"/>.
/// </summary>
public sealed class ChunkData
{
    public const int Size = 32;
    public const int Shift = 5; // log2(Size)
    public const int Volume = Size * Size * Size;

    private BlockId[]? _blocks;
    private BlockOrientation[]? _orientations;
    private BlockId _uniformBlock = BlockId.Air;
    private BlockOrientation _uniformOrientation = BlockOrientation.Upright;

    public bool IsDirty { get; set; }

    public BlockId Get(int x, int y, int z) => _blocks is { } b ? b[Index(x, y, z)] : _uniformBlock;
    public BlockOrientation GetOrientation(int x, int y, int z)
        => _orientations is { } o ? o[Index(x, y, z)] : _uniformOrientation;

    /// <summary>Whether every block is the same (then <paramref name="block"/>): as far as known without a scan,
    /// i.e. since the chunk was created or last <see cref="Compact"/>ed.</summary>
    public bool IsUniform(out BlockId block)
    {
        block = _uniformBlock;
        return _blocks == null;
    }

    public void Set(int x, int y, int z, BlockId id) => Set(x, y, z, id, BlockOrientation.Upright);

    public void Set(int x, int y, int z, BlockId id, BlockOrientation orientation)
    {
        int i = Index(x, y, z);
        if (_blocks == null && id != _uniformBlock) _blocks = Filled(_uniformBlock);
        if (_orientations == null && orientation != _uniformOrientation) _orientations = Filled(_uniformOrientation);
        if (_blocks != null) _blocks[i] = id;
        if (_orientations != null) _orientations[i] = orientation;
        IsDirty = true;
    }

    /// <summary>Drops the arrays if every block (or every orientation) is the same, e.g. after generating a chunk of
    /// solid stone.</summary>
    public void Compact()
    {
        if (_blocks != null && BlocksAsBytes().IndexOfAnyExcept((byte)_blocks[0]) < 0)
        {
            _uniformBlock = _blocks[0];
            _blocks = null;
        }
        if (_orientations != null && OrientationsAsBytes().IndexOfAnyExcept(_orientations[0].ToByte()) < 0)
        {
            _uniformOrientation = _orientations[0];
            _orientations = null;
        }
    }

    private static T[] Filled<T>(T value)
    {
        var a = new T[Volume];
        Array.Fill(a, value);
        return a;
    }

    public static int Index(int x, int y, int z) => x + Size * (y + Size * z);

    // Vectorized: the streaming survey calls this for every chunk it generates, and nearly all of them are air.
    public bool HasAnySolid() => _blocks == null ? _uniformBlock != BlockId.Air
                                                 : BlocksAsBytes().IndexOfAnyExcept((byte)BlockId.Air) >= 0;

    /// <summary>Zero-copy raw byte views of the block/orientation arrays, for bulk serialization (an orientation is
    /// its <see cref="BlockOrientation.ToByte"/>). A uniform chunk's view is a shared read-only array of its value.</summary>
    internal ReadOnlySpan<byte> BlocksAsBytes()
        => _blocks != null ? MemoryMarshal.Cast<BlockId, byte>(_blocks) : UniformBytes((byte)_uniformBlock);
    internal ReadOnlySpan<byte> OrientationsAsBytes()
        => _orientations != null ? MemoryMarshal.Cast<BlockOrientation, byte>(_orientations) : UniformBytes(_uniformOrientation.ToByte());

    private static readonly byte[]?[] Uniform = new byte[]?[256];

    private static byte[] UniformBytes(byte value)
    {
        if (Volatile.Read(ref Uniform[value]) is { } a) return a;
        a = new byte[Volume];
        Array.Fill(a, value);
        return Interlocked.CompareExchange(ref Uniform[value], a, null) ?? a;
    }

    /// <summary>Overwrites every block/orientation from a raw byte buffer previously produced by the
    /// matching <c>*AsBytes</c> method. Does not touch <see cref="IsDirty"/> — the caller decides
    /// what that should be afterward.</summary>
    internal void LoadBlockBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Volume)
            throw new ArgumentException($"Expected {Volume} bytes, got {bytes.Length}.", nameof(bytes));
        _blocks ??= new BlockId[Volume];
        bytes.CopyTo(MemoryMarshal.Cast<BlockId, byte>(_blocks));
    }

    internal void LoadOrientationBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Volume)
            throw new ArgumentException($"Expected {Volume} bytes, got {bytes.Length}.", nameof(bytes));
        _orientations ??= new BlockOrientation[Volume];
        for (int i = 0; i < bytes.Length; i++)
            _orientations[i] = BlockOrientation.FromByte(bytes[i]); // validated: a bad byte would index out of range
    }
}
