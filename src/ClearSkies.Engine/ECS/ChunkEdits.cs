using System.Collections.Concurrent;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Which chunks of the static world were edited since they were generated (so come from the store, not the generator),
/// and which of those hold a build, as layer bits per column within the streamed layers (<see cref="MinY"/> up, 64 of
/// them). Shared by streaming and the <see cref="VoxelsStage"/>: concurrent, since chunks are made on workers from it.
/// </summary>
public sealed class ChunkEdits(int minY)
{
    /// <summary>The lowest streamed layer: bit 0 of a column's bits.</summary>
    public readonly int MinY = minY;

    private readonly ConcurrentDictionary<ChunkPosition, byte> _edited = new();
    private readonly ConcurrentDictionary<(int x, int z), ulong> _editedLayers = new(); // the same, as layer bits per column
    private readonly ConcurrentDictionary<(int x, int z), ulong> _built = new();        // edited, with blocks

    public int Count => _edited.Count;

    public bool InLayers(int y) => y >= MinY && y <= MinY + 63;
    public ulong Bit(ChunkPosition p) => 1UL << (p.Y - MinY);

    public bool IsEdited(ChunkPosition p) => _edited.ContainsKey(p);
    public ulong EditedLayers(int x, int z) => _editedLayers.GetValueOrDefault((x, z));
    public ulong BuiltLayers(int x, int z) => _built.GetValueOrDefault((x, z));

    /// <summary>The full-detail layers under layer <paramref name="y"/> of level <paramref name="k"/>, as bits.</summary>
    public ulong FullLayersUnder(int k, int y)
    {
        ulong full = 0;
        for (int fy = y << k; fy < (y + 1) << k; fy++)
            if (InLayers(fy)) full |= 1UL << (fy - MinY);
        return full;
    }

    public void RecordEdited(ChunkPosition p)
    {
        _edited.TryAdd(p, 0);
        if (InLayers(p.Y)) _editedLayers[(p.X, p.Z)] = EditedLayers(p.X, p.Z) | Bit(p);
    }

    /// <summary>Records whether chunk <paramref name="p"/> holds a build; one emptied out doesn't.</summary>
    public void RecordBuild(ChunkPosition p, bool hasBlocks)
    {
        if (!InLayers(p.Y)) return;
        ulong built = BuiltLayers(p.X, p.Z);
        _built[(p.X, p.Z)] = hasBlocks ? built | Bit(p) : built & ~Bit(p);
    }
}
