using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>A cell's new block in a step of <see cref="WaterFlow"/>: water (with its <see cref="WaterLevel"/>) or air,
/// where water dried up or washed a plant away.</summary>
public readonly record struct WaterChange(Vector3D<int> Cell, BlockId Block, BlockOrientation State);

/// <summary>
/// How water flows through a volume, a step at a time, Minecraft style. Sources stay put. Water falls into any air (or
/// plant) under it as falling water; on ground (or other water) it spreads to its four sides one level weaker, as far as
/// <see cref="WaterLevel.Weakest"/>, only towards the nearest drop if there's one within <see cref="DropSearch"/>
/// blocks, so it runs downhill rather than pooling flat. Flowing water is only there while something feeds it (water
/// over it, or water beside it on ground that's a level stronger), and otherwise weakens a level a step until it dries
/// up, so a stream drains away once its source is gone. Flowing water between two sources, on ground or a source,
/// becomes a source itself (Minecraft's infinite water).
/// <para>Only woken cells are looked at: those next to a change (see <see cref="Wake"/>), so water at rest, every lake
/// generation made, costs nothing. A step reads the volume as it was and returns its changes, which the caller applies
/// (through a command, so every machine sees them) before the next step; the cells they touch are woken for it. Cells
/// whose chunk isn't loaded count as solid, so water stops at the edge of what's loaded.</para>
/// </summary>
public sealed class WaterFlow
{
    /// <summary>How far (blocks) water looks along the ground for a drop to run towards.</summary>
    public const int DropSearch = 4;

    private static readonly Vector3D<int>[] Sides = { new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1) };
    private static readonly Vector3D<int> Up = new(0, 1, 0);

    private readonly ChunkVolume _volume;
    private HashSet<Vector3D<int>> _woken = new(), _next = new();
    private readonly Dictionary<Vector3D<int>, (BlockId Block, BlockOrientation State)> _changes = new();
    private readonly List<WaterChange> _result = new();

    public WaterFlow(ChunkVolume volume) => _volume = volume;

    /// <summary>At most this many woken cells a step; the rest wait for the next.</summary>
    public int MaxCellsPerStep { get; set; } = 4096;

    /// <summary>Cells waiting to be looked at.</summary>
    public int Woken => _woken.Count;

    /// <summary>Wakes a cell and its six neighbours: something there changed.</summary>
    public void Wake(Vector3D<int> cell)
    {
        _woken.Add(cell);
        _woken.Add(cell + Up);
        _woken.Add(cell - Up);
        foreach (var d in Sides) _woken.Add(cell + d);
    }

    /// <summary>Wakes every cell in a box and those around it.</summary>
    public void Wake(Vector3D<int> min, Vector3D<int> max)
    {
        for (int z = min.Z - 1; z <= max.Z + 1; z++)
        for (int y = min.Y - 1; y <= max.Y + 1; y++)
        for (int x = min.X - 1; x <= max.X + 1; x++)
            _woken.Add(new Vector3D<int>(x, y, z));
    }

    /// <summary>Forgets every woken cell (on a machine that doesn't run the water).</summary>
    public void Clear() => _woken.Clear();

    /// <summary>Looks at the woken cells (up to <see cref="MaxCellsPerStep"/>) and returns how they change, waking the
    /// changed cells for the next step. The list is reused: consume it before the next step.</summary>
    public IReadOnlyList<WaterChange> Step()
    {
        _changes.Clear();
        _result.Clear();
        int done = 0;
        foreach (var cell in _woken)
        {
            if (done++ >= MaxCellsPerStep) _next.Add(cell); // left for the next step
            else Update(cell);
        }
        (_woken, _next) = (_next, _woken);
        _next.Clear();
        foreach (var (cell, c) in _changes)
        {
            _result.Add(new WaterChange(cell, c.Block, c.State));
            Wake(cell);
        }
        return _result;
    }

    private void Update(Vector3D<int> cell)
    {
        if (!TryGet(cell, out var block, out var state) || block != BlockId.Water) return;

        // Flowing water first takes the level what feeds it gives it, or dries up.
        if (!WaterLevel.IsSource(state) && MakesSource(cell))
        {
            Put(cell, BlockId.Water, WaterLevel.Source);
            state = WaterLevel.Source;
        }
        else if (!WaterLevel.IsSource(state))
        {
            if (!TryFeed(cell, out var fed)) { Put(cell, BlockId.Air, default); return; }
            if (fed != state) Put(cell, BlockId.Water, fed);
            state = fed;
        }

        // Then it falls, or on ground spreads.
        var below = cell - Up;
        if (TryGet(below, out var under, out var underState))
        {
            if (Open(under)) { Put(below, BlockId.Water, WaterLevel.Flowing(0, falling: true)); return; }
            if (under == BlockId.Water)
            {
                // Onto weaker flowing water it falls, making it falling water; onto any other water it joins it.
                if (!WaterLevel.IsSource(underState) && !WaterLevel.IsFalling(underState))
                    Put(below, BlockId.Water, WaterLevel.Flowing(0, falling: true));
                return;
            }
        }

        int spread = WaterLevel.SpreadLevel(state) + 1;
        if (spread > WaterLevel.Weakest) return;

        // Towards the nearest drop (if any is close), else every way.
        Span<int> cost = stackalloc int[Sides.Length];
        int best = int.MaxValue;
        for (int i = 0; i < Sides.Length; i++)
        {
            var n = cell + Sides[i];
            cost[i] = Passable(n) ? DropCost(n, Sides[i], 1) : int.MaxValue;
            best = System.Math.Min(best, cost[i]);
        }
        for (int i = 0; i < Sides.Length; i++)
            if (cost[i] != int.MaxValue && (cost[i] == best || best > DropSearch) && CanFlowInto(cell + Sides[i], spread))
                Put(cell + Sides[i], BlockId.Water, WaterLevel.Flowing(spread));
    }

    /// <summary>The state water at <paramref name="cell"/> would have from what feeds it: falling with water over it,
    /// else a level weaker than the strongest water beside it standing on ground.</summary>
    private bool TryFeed(Vector3D<int> cell, out BlockOrientation state)
    {
        if (TryGet(cell + Up, out var above, out _) && above == BlockId.Water)
        {
            state = WaterLevel.Flowing(0, falling: true);
            return true;
        }
        int level = FeedLevel(cell);
        state = WaterLevel.Flowing(System.Math.Min(level, WaterLevel.Weakest));
        return level <= WaterLevel.Weakest;
    }

    /// <summary>Whether flowing water becomes a source: with two or more sources beside it and solid ground or a
    /// source under it (Minecraft's infinite water).</summary>
    private bool MakesSource(Vector3D<int> cell)
    {
        if (!TryGet(cell - Up, out var under, out var underState) || Open(under)) return false;
        if (under == BlockId.Water && !WaterLevel.IsSource(underState)) return false;
        int sources = 0;
        foreach (var d in Sides)
            if (TryGet(cell + d, out var block, out var state) && block == BlockId.Water && WaterLevel.IsSource(state)) sources++;
        return sources >= 2;
    }

    private int FeedLevel(Vector3D<int> cell)
    {
        int level = int.MaxValue;
        foreach (var d in Sides)
        {
            var n = cell + d;
            if (!TryGet(n, out var block, out var state) || block != BlockId.Water) continue;
            // Water over a drop falls rather than spreading, so feeds nothing beside it.
            if (!TryGet(n - Up, out var under, out _) || Open(under)) continue;
            level = System.Math.Min(level, WaterLevel.SpreadLevel(state) + 1);
        }
        return level;
    }

    /// <summary>How many steps along the ground from <paramref name="cell"/> (reached in <paramref name="steps"/>,
    /// going <paramref name="dir"/>) the nearest drop is, or more than <see cref="DropSearch"/> if none is that close.</summary>
    private int DropCost(Vector3D<int> cell, Vector3D<int> dir, int steps)
    {
        if (IsDrop(cell)) return steps;
        if (steps >= DropSearch) return DropSearch + 1;
        int best = DropSearch + 1;
        foreach (var d in Sides)
        {
            if (d == -dir) continue;
            var n = cell + d;
            if (!Passable(n)) continue;
            best = System.Math.Min(best, DropCost(n, d, steps + 1));
        }
        return best;
    }

    /// <summary>Whether water reaching a cell would fall from it: air or a plant under it, or flowing water (where
    /// water already falls, so a stream keeps to its course once it has run down).</summary>
    private bool IsDrop(Vector3D<int> cell) =>
        TryGet(cell - Up, out var under, out var state) && (Open(under) || (under == BlockId.Water && !WaterLevel.IsSource(state)));

    /// <summary>Whether water could run on through a cell, looking for a drop: air, a plant, or flowing water.</summary>
    private bool Passable(Vector3D<int> cell) =>
        TryGet(cell, out var block, out var state) && (Open(block) || (block == BlockId.Water && !WaterLevel.IsSource(state)));

    /// <summary>Whether water at <paramref name="level"/> may flow into a cell: air, a plant (washed away), or weaker
    /// flowing water.</summary>
    private bool CanFlowInto(Vector3D<int> cell, int level)
    {
        if (!TryGet(cell, out var block, out var state)) return false;
        if (Open(block)) return true;
        return block == BlockId.Water && !WaterLevel.IsSource(state) && !WaterLevel.IsFalling(state) && WaterLevel.Level(state) > level;
    }

    /// <summary>Air, or a block water washes away (plants).</summary>
    private static bool Open(BlockId block) => block == BlockId.Air || BlockRegistry.Get(block).Replaceable;

    /// <summary>A change this step. Where two reach one cell the stronger water wins (any water over air).</summary>
    private void Put(Vector3D<int> cell, BlockId block, BlockOrientation state)
    {
        if (_changes.TryGetValue(cell, out var had) && Strength(had.Block, had.State) >= Strength(block, state)) return;
        if (TryGet(cell, out var now, out var nowState) && now == block && (block != BlockId.Water || nowState == state))
        {
            _changes.Remove(cell); // as it is
            return;
        }
        _changes[cell] = (block, state);
    }

    private static int Strength(BlockId block, BlockOrientation state) =>
        block != BlockId.Water ? 0 : WaterLevel.IsSource(state) ? 10 : WaterLevel.IsFalling(state) ? 9 : 8 - WaterLevel.Level(state);

    /// <summary>The block at a cell, if its chunk is loaded.</summary>
    private bool TryGet(Vector3D<int> cell, out BlockId block, out BlockOrientation state)
    {
        var pos = ChunkPosition.FromVoxel(cell);
        if (_volume.GetData(pos) is not { } data || pos.Y < _volume.EditableLayers.Min || pos.Y > _volume.EditableLayers.Max)
        {
            block = BlockId.Air;
            state = default;
            return false;
        }
        int lx = cell.X - pos.X * ChunkData.Size, ly = cell.Y - pos.Y * ChunkData.Size, lz = cell.Z - pos.Z * ChunkData.Size;
        block = data.Get(lx, ly, lz);
        state = data.GetOrientation(lx, ly, lz);
        return true;
    }
}
