using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Pipeline;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// The levels of detail streamed (see <see cref="SetLevels"/>). Level 0 is full detail, the world itself; level k is a
/// low-resolution copy of it (a <see cref="ChunkVolume"/> with Scale 2^k, see <see cref="LowResolutionCopy"/>), meshed,
/// lit and drawn by the same systems as the world, within a ball of its radius around the view. Every level streams
/// the same way (its own rings, scan, queue and unloading, see ChunkLoadSystem, and its own grid in the chunk pipeline);
/// they differ in where a chunk comes from. A coarse chunk is made from the full chunks under it (generated, or from
/// the store if edited), shrinking each into the level above it (<see cref="CoarseTerrain"/>), and again for each
/// level up to its own.
///
/// A level's chunk swaps with the eight of the level nearer the view under it (see <see cref="ShownStage"/>); every level
/// ends in whole 2x2x2 groups, so each chunk of the next swaps for its eight in one go. A coarse chunk's blocks are dropped once it's
/// meshed and in the GPU store (see <see cref="DropBlocksStage"/>): it's never edited or collided with.
/// </summary>
public sealed partial class ChunkLoadSystem
{
    /// <summary>One level of detail: its volume, how far it reaches, and its streaming.</summary>
    public sealed class TerrainLevel
    {
        internal TerrainLevel(int level, PipelineGrid grid, ChunkVolume volume, IChunkBudget? budget, int minY, int maxY)
        {
            Level = level;
            Grid = grid;
            Volume = volume;
            grid.Volume = volume;
            Budget = budget;
            MinY = minY;
            MaxY = maxY;
            AllLayers = ulong.MaxValue >> (63 - (maxY - minY));
        }

        /// <summary>0 for full detail, 1 for half resolution (2-block cells), 2 for a quarter, and so on.</summary>
        public int Level { get; }

        /// <summary>How far this level reaches (blocks).</summary>
        public float Radius { get; internal set; }

        public ChunkVolume Volume { get; }
        internal Entity Root => Volume.Root;

        /// <summary>Its chunks' grid in the chunk pipeline (the same grid for the level's every incarnation).</summary>
        internal readonly PipelineGrid Grid;
        internal ChunkKey Key(ChunkPosition p) => Grid.Key(p);

        internal TerrainLevel? Finer, Coarser;
        internal int Blocks => S << Level; // a chunk's size, in blocks
        internal bool Removed;

        /// <summary>What limits what's loaded (full detail's; a coarse level is limited by its radius).</summary>
        internal readonly IChunkBudget? Budget;

        /// <summary>The layers streamed, in this level's chunks: bit 0 of a column's bits is <see cref="MinY"/>.</summary>
        internal readonly int MinY, MaxY;
        internal readonly ulong AllLayers;
        internal ulong Bit(ChunkPosition p) => 1UL << (p.Y - MinY);

        // The interests as of the last rebuild, this frame's, and the chunk each is streamed around.
        internal readonly List<Ring> Rings = new(), Current = new();
        internal readonly Dictionary<Entity, (int x, int y, int z)> InterestChunks = new();
        internal int Rebuilds;

        // The rebuild's scan of the wanted columns: per ring, its offsets and the next one to look at; and whether it has
        // looked at them all (or filled the queue). Columns nearer an interest than the scan's position are all queued,
        // loading or loaded.
        internal (short dx, short dz, int d)[][] ScanOffsets = Array.Empty<(short, short, int)[]>();
        internal int[] ScanAt = Array.Empty<int>();
        internal bool ScanDone = true;

        /// <summary>Per wanted column (layer bits): what may hold something, and what turned out to be air. Dropped
        /// once the column is no longer wanted, so returning re-learns its air.</summary>
        internal readonly Dictionary<(int x, int z), (ulong Generated, ulong Air)> Columns = new();

        // Columns with chunks still to fetch, closest first, from QueueHead on.
        internal readonly List<(int x, int z)> Queue = new();
        internal int QueueHead;
        internal bool QueueTruncated;

        /// <summary>The chunks it wants of the pipeline; and of those, the ones not made yet, per column (a count and
        /// layer bits) and in all.</summary>
        internal readonly HashSet<ChunkPosition> Wanting = new();
        internal readonly Dictionary<(int x, int z), (int Chunks, ulong Layers)> InFlight = new();
        internal int InFlightChunks;

        // Chunks no longer wanted at the last rebuild, unloaded from UnloadAt on a few per frame.
        internal readonly List<ChunkPosition> ToUnload = new();
        internal int UnloadAt;

        // Loaded columns, farthest first, as of the first eviction since the last rebuild; EvictNext is the next to
        // consider (-1: not built since). NothingToEvict: the last search found nothing farther than the queue's head.
        internal readonly List<(int x, int z)> EvictOrder = new();
        internal int EvictNext = -1, Evictions;
        internal bool NothingToEvict, Full;
    }

    /// <summary>The levels of detail: full detail, then the coarse ones nearest the view first (see <see cref="SetLevels"/>).</summary>
    public IReadOnlyList<TerrainLevel> Levels => _levels;

    /// <summary>Whether every coarse level has made all its columns in range (nothing queued or in flight).</summary>
    internal bool LevelsIdle =>
        _levels.Skip(1).All(l => l.Rings.Count > 0 && l.ScanDone && l.QueueHead == l.Queue.Count && l.InFlightChunks == 0);

    /// <summary>Draws the view's distant terrain coarse: full detail to <paramref name="fullDetailRadius"/> (blocks),
    /// then a level per radius in <paramref name="radii"/> (level k to the k-th, in blocks, each farther than the
    /// last, and at most <see cref="GridStore.ScaledReach"/>). An empty list turns it off: full detail as far as the
    /// view. With as many levels as before, only the radii change (each level streams to match, as when the view
    /// moves); otherwise the coarse levels are made anew.</summary>
    public void SetLevels(float fullDetailRadius, IReadOnlyList<float> radii)
    {
        if (radii.Count != _levels.Count - 1)
        {
            foreach (var level in _levels.Skip(1)) RemoveLevel(level);
            _levels.RemoveRange(1, _levels.Count - 1);
            FullDetail.Coarser = null;
        }
        if (radii.Count >= MaxLevels) throw new ArgumentOutOfRangeException(nameof(radii), $"At most {MaxLevels - 1} coarse levels.");
        if (radii.Count == 0)
        {
            FullDetail.Radius = _viewDistance;
            return;
        }

        FullDetail.Radius = MathF.Max(fullDetailRadius, 2 * S);
        for (int k = 1; k <= radii.Count; k++)
        {
            var finer = _levels[k - 1];
            if (k == _levels.Count) _levels.Add(NewLevel(k, finer));
            var level = _levels[k];
            level.Radius = MathF.Min(MathF.Max(radii[k - 1], finer.Radius + (S << k)), GridStore.ScaledReach);
            level.Volume.RankedFrom = finer.Radius;
        }
    }

    /// <summary>Coarse level <paramref name="k"/>, over <paramref name="finer"/>: a low-resolution copy of the world,
    /// drawn.</summary>
    private TerrainLevel NewLevel(int k, TerrainLevel finer)
    {
        var root = _world.CreateEntity();
        var volume = ChunkVolume.CreateLowResolutionCopy(root, _world, 1 << k);
        root.Set<Rendered>();
        var level = new TerrainLevel(k, _grids[k], volume, null, _minY >> k, (_minY + 63) >> k) { Finer = finer };
        finer.Coarser = level;
        return level;
    }

    private void RemoveLevel(TerrainLevel level)
    {
        level.Removed = true;
        foreach (var pos in level.Wanting) Forget(level, pos);
        foreach (var (pos, _) in level.Volume.All) // held by the pipeline, let go of already
            if (!level.Wanting.Contains(pos) && _pipeline.Get(level.Key(pos)) != null) Forget(level, pos);
        level.Wanting.Clear();
        foreach (var (pos, _) in level.Volume.All.ToList()) Unload(level, pos);
        if (level.Root.IsAlive) level.Root.Dispose();
    }

    /// <summary>Whether a level's chunk is in its range (see <see cref="Wanted(TerrainLevel, ChunkPosition)"/>), by its
    /// pipeline key; and whether the level has it (see <see cref="Has(TerrainLevel, ChunkPosition)"/>). False for any
    /// other grid's.</summary>
    internal bool InRange(ChunkKey key) => LevelOf(key) is { } level && Wanted(level, key.Pos);
    internal bool Has(ChunkKey key) => LevelOf(key) is { } level && Has(level, key.Pos);

    private TerrainLevel? LevelOf(ChunkKey key) => key.Grid < _levels.Count && _levels[key.Grid].Grid.Id == key.Grid ? _levels[key.Grid] : null;

    /// <summary>Whether the level has chunk <paramref name="p"/>: it's wanted, and loaded or known to be air.</summary>
    internal bool Has(TerrainLevel level, ChunkPosition p)
    {
        if (!Wanted(level, p)) return false;
        if (!level.Columns.TryGetValue((p.X, p.Z), out var col)) return false;
        ulong maybe = (col.Generated & ~col.Air) | (level.Level == 0 ? _edits.BuiltLayers(p.X, p.Z) : 0);
        return (maybe & level.Bit(p)) == 0 || level.Volume.IsLoaded(p);
    }

    /// <summary>The edited full chunks that making the level's chunks <paramref name="work"/> reads: in full detail,
    /// those of them edited; in a coarse level, the edited ones under them.</summary>
    private IEnumerable<ChunkPosition> EditedUnder(TerrainLevel level, List<(ChunkPosition Pos, bool Edited)> work)
    {
        foreach (var (pos, edited) in work)
        {
            if (level.Level == 0)
            {
                if (edited) yield return pos;
                continue;
            }
            int n = 1 << level.Level;
            ulong under = _edits.FullLayersUnder(level.Level, pos.Y);
            for (int j = 0; j < n * n; j++)
            {
                int x = pos.X * n + j % n, z = pos.Z * n + j / n;
                for (ulong bits = _edits.EditedLayers(x, z) & under; bits != 0; bits &= bits - 1)
                    yield return new ChunkPosition(x, _minY + BitOperations.TrailingZeroCount(bits), z);
            }
        }
    }

    internal void DrawLevelsUi()
    {
        ImGui.Separator();
        ImGui.Text($"Full detail to {FullDetailRadius:F0} blocks");
        foreach (var level in _levels.Skip(1))
            ImGui.Text($"Level {level.Level} (x{1 << level.Level}) to {level.Radius:F0} blocks: {level.Columns.Count} columns known, " +
                       $"{level.Queue.Count - level.QueueHead} queued, {level.InFlightChunks} chunks being made, " +
                       $"{level.Volume.LoadedCount:N0} chunks");
    }
}
