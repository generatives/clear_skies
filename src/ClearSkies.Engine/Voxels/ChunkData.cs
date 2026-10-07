using System;
using ClearSkies.Engine.Core;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

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

    // How many of _blocks are not air, how many collide (BlockDef.Collides) and how many are opaque cubes (see IsAllOpaque),
    // kept up to date by every write so HasAnyNonAir, HasAnyColliding and IsAllOpaque never scan. Only meaningful while
    // _blocks exists: a uniform chunk's follow from _uniformBlock.
    private int _nonAir, _colliding, _opaque;

    public bool IsDirty { get; set; }

    // The owner's reference plus one per background job reading this chunk (see Retain); the arrays go back to the
    // pool once the owner has released the chunk and the last job is done with it.
    private int _refs = 1;

    /// <summary>Keeps the arrays from being recycled while a background job reads them; pair with
    /// <see cref="Unretain"/> when the job is done.</summary>
    public void Retain() => Interlocked.Increment(ref _refs);
    public void Unretain() { if (Interlocked.Decrement(ref _refs) == 0) Recycle(); }

    /// <summary>The owner is done with this chunk (it was unloaded): its arrays return to the pool for the next chunk
    /// once no background job is reading them. Only for a chunk nothing else will keep using; anyone still holding it
    /// afterwards sees all air.</summary>
    public void Release() => Unretain();

    private void Recycle()
    {
        BlockPool.Return(Interlocked.Exchange(ref _blocks, null));
        OrientationPool.Return(Interlocked.Exchange(ref _orientations, null));
        _uniformBlock = BlockId.Air;
        _uniformOrientation = BlockOrientation.Upright;
    }

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
        if (_blocks == null && id != _uniformBlock)
        {
            _blocks = Filled(BlockPool, _uniformBlock);
            _nonAir = _uniformBlock != BlockId.Air ? Volume : 0;
            _colliding = Colliding[(byte)_uniformBlock] ? Volume : 0;
            _opaque = Opaque[(byte)_uniformBlock] ? Volume : 0;
        }
        if (_orientations == null && orientation != _uniformOrientation) _orientations = Filled(OrientationPool, _uniformOrientation);
        if (_blocks != null)
        {
            var old = _blocks[i];
            if (old != id)
            {
                _nonAir += (id != BlockId.Air ? 1 : 0) - (old != BlockId.Air ? 1 : 0);
                _colliding += (Colliding[(byte)id] ? 1 : 0) - (Colliding[(byte)old] ? 1 : 0);
                _opaque += (Opaque[(byte)id] ? 1 : 0) - (Opaque[(byte)old] ? 1 : 0);
                _blocks[i] = id;
            }
        }
        if (_orientations != null) _orientations[i] = orientation;
        IsDirty = true;
    }

    /// <summary>Drops the arrays (back to the pool) if every block (or every orientation) is the same, e.g. after
    /// generating a chunk of solid stone. Only before the chunk is shared with other threads.</summary>
    public void Compact()
    {
        if (_blocks != null && BlocksAsBytes().IndexOfAnyExcept((byte)_blocks[0]) < 0)
        {
            _uniformBlock = _blocks[0];
            BlockPool.Return(_blocks);
            _blocks = null;
        }
        if (_orientations != null && OrientationsAsBytes().IndexOfAnyExcept(_orientations[0].ToByte()) < 0)
        {
            _uniformOrientation = _orientations[0];
            OrientationPool.Return(_orientations);
            _orientations = null;
        }
    }

    private static T[] Filled<T>(FixedArrayPool<T> pool, T value)
    {
        var a = pool.Rent();
        Array.Fill(a, value);
        return a;
    }

    // Chunk-sized arrays from unloaded (or compacted) chunks, for the next chunks to load. While streaming, as many
    // chunks unload as load, so after a while nearly every chunk reuses an array instead of allocating one: fewer
    // allocations means fewer full collections, and those were the long pauses. New arrays go on the pinned object
    // heap, where a collection never copies them. 1024 kept is 32 MB of each; streaming needs far fewer spare at once.
    private static readonly FixedArrayPool<BlockId> BlockPool = new(Volume, maxKept: 1024, pinned: true);
    private static readonly FixedArrayPool<BlockOrientation> OrientationPool = new(Volume, maxKept: 1024, pinned: true);

    public static int Index(int x, int y, int z) => x + Size * (y + Size * z);

    /// <summary>Whether any block isn't air. Kept count of, so it's as cheap for a dug-out chunk (all air, but with its
    /// array still) as for a uniform one.</summary>
    public bool HasAnyNonAir() => _blocks == null ? _uniformBlock != BlockId.Air : _nonAir > 0;

    /// <summary>Whether any block collides (<see cref="BlockDef.Collides"/>): what a collider would be built from.
    /// False for a chunk of air and passable blocks only (e.g. levers), which has blocks but no collider.</summary>
    public bool HasAnyColliding() => _blocks == null ? Colliding[(byte)_uniformBlock] : _colliding > 0;

    /// <summary>How many blocks collide (<see cref="BlockDef.Collides"/>), out of <see cref="Volume"/>. Kept count of, like
    /// <see cref="HasAnyColliding"/>: wind reads it for how much of a chunk is terrain.</summary>
    public int CollidingCount => _blocks == null ? (Colliding[(byte)_uniformBlock] ? Volume : 0) : _colliding;

    /// <summary>Whether every block is an opaque cube (<see cref="BlockDef.IsFullCube"/>, not
    /// <see cref="BlockDef.Transparent"/>): nothing inside can be seen, and it hides every face against it. Kept count
    /// of, like <see cref="HasAnyColliding"/>.</summary>
    public bool IsAllOpaque => _blocks == null ? Opaque[(byte)_uniformBlock] : _opaque == Volume;

    // BlockDef.Collides and IsAllOpaque's test by block id, so Set's bookkeeping is a few array reads.
    private static readonly bool[] Colliding = Build(d => d.Collides);
    private static readonly bool[] Opaque = Build(d => d.IsFullCube && !d.Transparent);

    private static bool[] Build(Func<BlockDef, bool> test)
    {
        var t = new bool[256];
        for (int i = 0; i < t.Length; i++) t[i] = test(BlockRegistry.Get((BlockId)i));
        return t;
    }

    /// <summary>Zero-copy raw byte views of the block/orientation arrays, for bulk serialization (an orientation is
    /// its <see cref="BlockOrientation.ToByte"/>). A uniform chunk's view is a shared read-only array of its value.</summary>
    public ReadOnlySpan<byte> BlocksAsBytes()
        => _blocks != null ? MemoryMarshal.Cast<BlockId, byte>(_blocks) : UniformBytes((byte)_uniformBlock);
    public ReadOnlySpan<byte> OrientationsAsBytes()
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
        _blocks ??= BlockPool.Rent();
        bytes.CopyTo(MemoryMarshal.Cast<BlockId, byte>(_blocks));
        _nonAir = Volume - bytes.Count((byte)BlockId.Air);
        _colliding = _opaque = 0;
        foreach (byte b in bytes)
        {
            if (Colliding[b]) _colliding++;
            if (Opaque[b]) _opaque++;
        }
    }

    internal void LoadOrientationBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Volume)
            throw new ArgumentException($"Expected {Volume} bytes, got {bytes.Length}.", nameof(bytes));
        _orientations ??= OrientationPool.Rent();
        for (int i = 0; i < bytes.Length; i++)
            _orientations[i] = BlockOrientation.FromByte(bytes[i]); // validated: a bad byte would index out of range
    }
}
