using ClearSkies.Engine.Rendering;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>A light-emitting voxel within a chunk: chunk-local coordinates, emission level and block (for its
/// light colour).</summary>
internal readonly record struct EmitterVoxel(byte Lx, byte Ly, byte Lz, byte Level, BlockId Block);

internal sealed class ChunkEntry
{
    public ChunkData  Data        { get; }
    public ChunkVolume Volume { get; }
    public ChunkPosition Position { get; }
    public Entity     Entity      { get; }

    /// <summary>Light-emitting voxels in this chunk, rebuilt from <see cref="Data"/> whenever its opacity is
    /// repacked (see <c>GridStore.UploadChunk</c>). Gathered into the lamp list each frame.</summary>
    public List<EmitterVoxel> Emitters { get; } = new();

    /// <summary>This chunk's block entities (see <see cref="BlockDef.Components"/>) by chunk-local cell, owned by
    /// <see cref="ChunkVolume"/>. Null until the chunk gets its first one; most chunks never do.</summary>
    public Dictionary<Vector3D<int>, Entity>? BlockEntities { get; set; }

    /// <summary>Packed opacity words (see <c>GridStore.WordsPerChunk</c>). Null means "recompute from
    /// <see cref="Data"/>" — set on creation and invalidated on every block edit (see
    /// <c>ChunkVolume.SetBlock</c>).</summary>
    public uint[]? PackedOpacityWords { get; set; }

    /// <summary>One bit per 8³ brick of this chunk (bit = bx + 4*(by + 4*bz)): brick contains any opaque
    /// voxel / any non-opaque voxel. Computed alongside <see cref="PackedOpacityWords"/>; decides which bricks
    /// get light storage.</summary>
    public ulong BrickSolidMask { get; set; }
    public ulong BrickAirMask   { get; set; }

    /// <summary>Chunk-local bounds (inclusive) of the blocks edited since the last GPU upload, so lighting relights
    /// around just those instead of the whole chunk. <see cref="HasEdits"/> false: nothing edited (a fresh load).</summary>
    public bool HasEdits { get; private set; }
    public (int X, int Y, int Z) EditMin { get; private set; }
    public (int X, int Y, int Z) EditMax { get; private set; }

    /// <summary>Whether any of those edits placed a light-blocking block (which can darken, and so leave stale
    /// bounce light behind); breaking blocks can only brighten.</summary>
    public bool EditsAddedSolid { get; private set; }

    public void AddEdit(int lx, int ly, int lz, bool placedSolid)
    {
        if (placedSolid) EditsAddedSolid = true;
        if (!HasEdits) { EditMin = EditMax = (lx, ly, lz); HasEdits = true; return; }
        EditMin = (System.Math.Min(EditMin.X, lx), System.Math.Min(EditMin.Y, ly), System.Math.Min(EditMin.Z, lz));
        EditMax = (System.Math.Max(EditMax.X, lx), System.Math.Max(EditMax.Y, ly), System.Math.Max(EditMax.Z, lz));
    }

    public void ClearEdits() { HasEdits = false; EditsAddedSolid = false; }

    public ChunkEntry(ChunkData data, Entity entity, ChunkVolume volume, ChunkPosition position)
    {
        Data   = data;
        Entity = entity;
        Volume = volume;
        Position = position;
    }
}
