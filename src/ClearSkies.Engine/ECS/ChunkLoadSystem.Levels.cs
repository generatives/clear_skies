using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// The levels of detail streamed (see <see cref="SetLevels"/>). Level 0 is full detail, the world itself; level k is a
/// low-resolution copy of it (a <see cref="ChunkVolume"/> with Scale 2^k, see <see cref="LowResolutionCopy"/>), meshed,
/// lit and drawn by the same systems as the world, within a ball of its radius around the view. Every level streams
/// the same way (its own rings, scan, queue, jobs and unloading, see ChunkLoadSystem); they differ in where a chunk
/// comes from. A coarse level's column job makes its missing chunks from the full chunks under them (generated, or
/// from the store if edited), shrinking each into the level above it (<see cref="CoarseTerrain"/>), and again for each
/// level up to its own.
///
/// A level's chunk swaps with the eight of the level nearer the view under it (see <see cref="TerrainSwapSystem"/>); every level
/// ends in whole 2x2x2 groups, so each chunk of the next swaps for its eight in one go. A coarse chunk's blocks are dropped once it's
/// meshed and in the GPU store: it's never edited or collided with.
/// </summary>
public sealed partial class ChunkLoadSystem
{
    /// <summary>One level of detail: its volume, how far it reaches, and its streaming.</summary>
    public sealed class TerrainLevel
    {
        internal TerrainLevel(int level, ChunkVolume volume, IChunkBudget? budget, int minY, int maxY)
        {
            Level = level;
            Volume = volume;
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
        // loading or loaded. Per ring, how far around it was known to be loaded or queued before the rebuild (full
        // detail's, for TerrainScanned).
        internal (short dx, short dz, int d)[][] ScanOffsets = Array.Empty<(short, short, int)[]>();
        internal int[] ScanAt = Array.Empty<int>();
        internal float[] ScanCarry = Array.Empty<float>();
        internal bool ScanDone = true;

        /// <summary>Per wanted column (layer bits): what may hold something, and what turned out to be air. Dropped
        /// once the column is no longer wanted, so returning re-learns its air.</summary>
        internal readonly Dictionary<(int x, int z), (ulong Generated, ulong Air)> Columns = new();

        // Columns with chunks still to fetch, closest first, from QueueHead on; and the columns workers have now, with
        // their chunks as a count and layer bits.
        internal readonly List<(int x, int z)> Queue = new();
        internal int QueueHead;
        internal bool QueueTruncated, SkippedInFlight;
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

        internal readonly HashSet<ChunkPosition> ToDrop = new(); // loaded chunks whose blocks can go once they're meshed and uploaded
        internal int Dropped;

        /// <summary>Chunks no longer wanted but held loaded (see <see cref="KeepLoaded"/>), with the frame each was
        /// first held.</summary>
        internal readonly Dictionary<ChunkPosition, int> Held = new();
    }

    /// <summary>Most coarse-level column jobs in flight at once: they're slow (a level-1 column generates four full
    /// columns, level 2 sixteen), so fewer than the workers, leaving a worker for full detail.</summary>
    private static readonly int MaxLevelJobs = System.Math.Max(1, BackgroundWork.Workers - 1);

    /// <summary>Coarse chunks looked at a frame for dropping their blocks.</summary>
    private const int DropChecksPerFrame = 256;

    private int _levelJobs; // coarse column jobs in flight, of levels since removed too

    /// <summary>The levels of detail: full detail, then the coarse ones nearest the view first (see <see cref="SetLevels"/>).</summary>
    public IReadOnlyList<TerrainLevel> Levels => _levels;

    /// <summary>Whether every coarse level has made all its columns in range (nothing queued or in flight).</summary>
    internal bool LevelsIdle => _levelJobs == 0 &&
        _levels.Skip(1).All(l => l.Rings.Count > 0 && l.ScanDone && l.QueueHead == l.Queue.Count);

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
        var level = new TerrainLevel(k, volume, null, _minY >> k, (_minY + 63) >> k) { Finer = finer };
        finer.Coarser = level;
        return level;
    }

    private void RemoveLevel(TerrainLevel level)
    {
        level.Removed = true;
        foreach (var (pos, _) in level.Volume.All.ToList()) Unload(level, pos);
        if (level.Root.IsAlive) level.Root.Dispose();
    }

    /// <summary>Whether the level has chunk <paramref name="p"/>: it's wanted, and loaded or known to be air.</summary>
    internal bool Has(TerrainLevel level, ChunkPosition p)
    {
        if (!Wanted(level, p)) return false;
        if (!level.Columns.TryGetValue((p.X, p.Z), out var col)) return false;
        ulong maybe = (col.Generated & ~col.Air) | (level.Level == 0 ? _built.GetValueOrDefault((p.X, p.Z)) : 0);
        return (maybe & level.Bit(p)) == 0 || level.Volume.IsLoaded(p);
    }

    /// <summary>The edited full chunks a column's job needs: in full detail, its own; in a coarse level, those under
    /// its chunks being made.</summary>
    private IEnumerable<ChunkPosition> EditedUnder(TerrainLevel level, (int x, int z) col, List<(ChunkPosition Pos, bool Edited)> work)
    {
        if (level.Level == 0)
        {
            foreach (var (pos, edited) in work)
                if (edited) yield return pos;
            yield break;
        }
        ulong layers = 0;
        foreach (var (pos, _) in work) layers |= level.Bit(pos);
        ulong under = FullLayersUnder(level, layers);
        int n = 1 << level.Level;
        for (int j = 0; j < n * n; j++)
        {
            int x = col.x * n + j % n, z = col.z * n + j / n;
            for (ulong bits = _editedLayers.GetValueOrDefault((x, z)) & under; bits != 0; bits &= bits - 1)
                yield return new ChunkPosition(x, _minY + BitOperations.TrailingZeroCount(bits), z);
        }
    }

    /// <summary>The full layers (bits from the lowest streamed one) under the level's <paramref name="layers"/>.</summary>
    private ulong FullLayersUnder(TerrainLevel level, ulong layers)
    {
        ulong full = 0;
        for (; layers != 0; layers &= layers - 1)
        {
            int y = level.MinY + BitOperations.TrailingZeroCount(layers);
            for (int fy = y << level.Level; fy < (y + 1) << level.Level; fy++)
                if (fy >= _minY && fy <= _minY + 63) full |= 1UL << (fy - _minY);
        }
        return full;
    }

    /// <summary>Makes a coarse column's missing chunks (<paramref name="layers"/>) on a worker: each full column under
    /// it a full column at a time, bottom to top (the generator profiles a column once for all its chunks), only the
    /// layers under those chunks, each full chunk shrunk into the level-1 chunk over it as it's made; then each level
    /// shrunk into the next. A chunk that comes out air is reported as such.</summary>
    private void DispatchCoarseColumn(TerrainLevel level, (int x, int z) col, ulong layers)
    {
        _levelJobs++;
        int n = 1 << level.Level, steps = level.Level, minY = _minY;
        ulong under = FullLayersUnder(level, layers);
        // What the job needs of the main thread's state, captured now: each full column's builds and edits.
        var built = new ulong[n * n];
        var editedBits = new ulong[n * n];
        for (int j = 0; j < n * n; j++)
        {
            var key = (col.x * n + j % n, col.z * n + j / n);
            built[j] = _built.GetValueOrDefault(key);
            editedBits[j] = _editedLayers.GetValueOrDefault(key);
        }
        BackgroundWork.Queue(() =>
        {
            var made = new List<(ChunkPosition Pos, ChunkData? Data)>();
            try
            {
                var coarse = new Dictionary<ChunkPosition, ChunkData>();
                for (int j = 0; j < n * n; j++)
                {
                    int x = col.x * n + j % n, z = col.z * n + j / n;
                    ulong bits = (_generator.Value!.ColumnLayers(x, z, minY) | built[j] | editedBits[j]) & under;
                    for (; bits != 0; bits &= bits - 1)
                    {
                        int layer = BitOperations.TrailingZeroCount(bits);
                        var pos = new ChunkPosition(x, minY + layer, z);
                        var full = new ChunkData();
                        if (!((editedBits[j] >> layer & 1) != 0 && _chunkStore.TryLoad(pos, full)))
                            _generator.Value!.Generate(full, pos);
                        if (full.HasAnyNonAir()) CoarseTerrain.ShrinkInto(coarse, pos, full, majority: true, plants: true);
                        full.Release();
                    }
                }
                for (int step = 2; step <= steps; step++)
                {
                    var finer = coarse;
                    coarse = new Dictionary<ChunkPosition, ChunkData>();
                    foreach (var (pos, chunk) in finer)
                    {
                        if (chunk.HasAnyNonAir()) CoarseTerrain.ShrinkInto(coarse, pos, chunk, majority: true, plants: true);
                        chunk.Release();
                    }
                }
                for (ulong bits = layers; bits != 0; bits &= bits - 1)
                {
                    var pos = new ChunkPosition(col.x, level.MinY + BitOperations.TrailingZeroCount(bits), col.z);
                    if (coarse.Remove(pos, out var chunk))
                    {
                        chunk.Compact();
                        if (chunk.HasAnyNonAir())
                        {
                            chunk.IsDirty = false;
                            made.Add((pos, chunk));
                            continue;
                        }
                        chunk.Release();
                    }
                    made.Add((pos, null));
                }
                foreach (var (_, chunk) in coarse) chunk.Release();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[load] level {level.Level} column {col} failed: {e}");
                foreach (var (_, data) in made) data?.Release();
                made.Clear();
            }
            _results.Enqueue((level, col, Prepare(made)));
        });
    }

    /// <summary>Whether a coarse chunk's blocks can go once it's meshed and uploaded. A chunk all one block, or of
    /// opaque cubes, keeps them: they're small, or say it's buried. So does one with see-through blocks on a side,
    /// which remeshes when a neighbour comes.</summary>
    private static bool CanDrop(ChunkData data)
    {
        if (data.IsUniform(out _) || data.IsAllOpaque) return false;
        for (int f = 0; f < 6; f++)
            if (ChunkVolume.FaceHasTransparent(data, f)) return false;
        return true;
    }

    /// <summary>Drops the blocks of coarse chunks that are meshed and in the GPU store, a few a frame.</summary>
    private void DropSome()
    {
        int checks = DropChecksPerFrame;
        foreach (var level in _levels)
        {
            if (level.ToDrop.Count == 0) continue;
            var done = new List<ChunkPosition>();
            foreach (var pos in level.ToDrop)
            {
                if (checks-- <= 0) break;
                var entry = level.Volume.GetEntry(pos);
                if (entry == null) { done.Add(pos); continue; }
                var e = entry.Entity;
                if (!e.Has<ChunkMeshedFlag>() || !e.Has<ChunkUploadedFlag>() || e.Has<NeedsRemeshFlag>() || e.Has<NeedsGpuUploadFlag>())
                    continue;
                entry.DropData();
                level.Dropped++;
                done.Add(pos);
            }
            foreach (var pos in done) level.ToDrop.Remove(pos);
        }
    }

    internal void DrawLevelsUi()
    {
        ImGui.Separator();
        ImGui.Text($"Full detail to {FullDetailRadius:F0} blocks; coarse level jobs in flight: {_levelJobs}");
        foreach (var level in _levels.Skip(1))
            ImGui.Text($"Level {level.Level} (x{1 << level.Level}) to {level.Radius:F0} blocks: {level.Columns.Count} columns known, " +
                       $"{level.Queue.Count - level.QueueHead} queued, {level.InFlight.Count} in flight, " +
                       $"{level.Volume.LoadedCount:N0} chunks ({level.Dropped:N0} with blocks dropped)");
    }
}
