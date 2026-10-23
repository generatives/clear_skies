using ClearSkies.Engine.Core;
using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;
using ImGuiNET;
using TerrainLevel = ClearSkies.Engine.ECS.ChunkLoadSystem.TerrainLevel;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Swaps between the levels of detail the chunk loader streams (see <see cref="ChunkLoadSystem.SetLevels"/>), as two
/// chunk pipeline stages on each coarse chunk (see <see cref="AddStages"/>), drawn for:
/// <list type="bullet">
/// <item>Cover: while the finer level has the eight chunks under it in the light store (or air), it's covered there
/// (<see cref="SetCovered"/>): the finer grid's rays pass through it, so each level is lit by its own blocks.</item>
/// <item>Swap: once it's shown, it's drawn until the eight are shown and lit, then dissolves out over
/// <see cref="FadeSeconds"/> as they dissolve in, each pixel showing one or the other; once the finer level lets go of
/// them, it dissolves back in over them. It holds them (<see cref="ChunkPipeline.Hold"/>) while it isn't drawn, and until
/// it's back, so they don't vanish first. One first shown where the finer level wants all eight isn't drawn: it would
/// cover finer chunks already shown, and be lit in place of them, till the rest come, which they soon do.</item>
/// </list>
/// Every level's chunks are drawn by what this says (<see cref="DrawState"/>, on each level's root as
/// <see cref="ChunkDrawStates"/>), full detail's while there are coarse levels.
/// </summary>
public sealed class TerrainSwapSystem(ChunkLoadSystem load) : ISystem
{
    private readonly ChunkLoadSystem _load = load;
    /// <summary>A coarse chunk's swap with the finer level under it: the Swap stage's output.</summary>
    private sealed class Swap(ChunkKey key)
    {
        public readonly ChunkKey Key = key;
        public bool Drawn; // its eight aren't all shown and lit, or it's never been drawn (see the class summary)
        public double FadeAt = double.NegativeInfinity; // when it last swapped in or out
        public byte Held; // which of the eight it holds, a bit each (see Relation.Child)
    }

    private readonly Dictionary<TerrainLevel, ChunkDrawStateOf> _drawStates = new();
    private readonly List<TerrainLevel> _gone = new();
    private ChunkPipeline? _pipeline;
    private SwapStage? _swap;
    private CoverStage? _cover;
    private Stage? _store, _shown;
    private double _time;

    /// <summary>How long a swap between detail levels takes to dissolve one into the other (seconds; 0: at once).</summary>
    public float FadeSeconds { get; set; } = 2f;

    /// <summary>Whether a chunk of a grid is lit (GpuLightSystem.IsLit): a swap out waits for the finer chunks' light.
    /// Without it, everything counts as lit.</summary>
    public Func<GridHandle, ChunkPosition, bool>? IsLit { get; set; }

    /// <summary>Puts a chunk of a grid ahead of the light queues (GpuLightSystem.ShowFirst): the finer chunks a swap
    /// out waits for.</summary>
    public Action<GridHandle, ChunkPosition>? ShowFirst { get; set; }

    /// <summary>Tells the light store a coarse chunk is covered by the finer level, or not (GridStore.SetCovered).</summary>
    public Action<GridHandle, ChunkPosition, bool>? SetCovered { get; set; }

    private IReadOnlyList<TerrainLevel> Levels => _load.Levels;

    /// <summary>Adds the stages to the pipeline: covers read <paramref name="store"/> (a chunk in the light store),
    /// swaps <paramref name="shown"/> (a chunk ready to be seen, see <see cref="ShownStage"/>).</summary>
    public void AddStages(ChunkPipeline pipeline, Stage store, Stage shown)
    {
        _pipeline = pipeline;
        (_store, _shown) = (store, shown);
        _cover = pipeline.Add(new CoverStage(this));
        _swap = pipeline.Add(new SwapStage(this));
    }

    /// <summary>Keeps the time, and each level's draw states on its root, full detail's while there are coarse levels.</summary>
    public void Update(float dt)
    {
        _time += dt;
        bool levels = Levels.Count > 1;
        foreach (var (level, _) in _drawStates)
            if (level.Removed || !levels) _gone.Add(level);
        foreach (var level in _gone)
        {
            if (level.Root.IsAlive && level.Root.Has<ChunkDrawStates>()) level.Root.Remove<ChunkDrawStates>();
            _drawStates.Remove(level);
        }
        _gone.Clear();
        if (!levels) return;
        foreach (var level in Levels)
        {
            if (_drawStates.ContainsKey(level)) continue;
            ChunkDrawStateOf of = p => DrawState(level, p);
            _drawStates[level] = of;
            level.Root.Set(new ChunkDrawStates { Of = of });
        }
    }

    /// <summary>How chunk <paramref name="p"/> of <paramref name="level"/> is drawn: for a coarse level, coming in over
    /// the finer level it replaces (drawing the pixels that leaves out), drawn, going away (leaving out the pixels the
    /// finer level coming in draws), or left out; and for any level, hidden the same way by a coarser level over it.</summary>
    public ChunkDrawState DrawState(TerrainLevel level, ChunkPosition p)
    {
        float dissolve = 0f;
        if (level.Level > 0)
        {
            if (SwapOf(level, p) is not { } swap) return ChunkDrawState.Hidden; // not shown yet
            float t = Faded(swap), share = swap.Drawn ? t : 1f - t;
            if (share <= 0f) return ChunkDrawState.Hidden;
            dissolve = swap.Drawn ? (share < 1f ? -share : 0f) : 1f - share;
        }
        if (level.Coarser is { } coarser)
        {
            float over = CoverOf(coarser, p);
            if (over >= 1f) return ChunkDrawState.Hidden;
            if (over != 0f) dissolve = over;
        }
        return dissolve == 0f ? ChunkDrawState.Whole : new ChunkDrawState(dissolve);
    }

    /// <summary>How much the finer level's chunk <paramref name="finer"/> is hidden by <paramref name="level"/> over
    /// it: 1 while drawn, rising from 0 to 1 over <see cref="FadeSeconds"/> as it comes in, 0 when it isn't; while it
    /// dissolves away, -t: the finer chunk draws only the share t of pixels it has left. Where this level hides nothing
    /// there, a coarser one may (it's drawn where this one isn't shown).</summary>
    private float CoverOf(TerrainLevel level, ChunkPosition finer)
    {
        var p = Relation.Over(finer);
        float own = 0f;
        if (SwapOf(level, p) is { } swap)
        {
            float t = Faded(swap);
            own = swap.Drawn ? t : t >= 1f ? 0f : t <= 0f ? 1f : -t;
        }
        return own == 0f && level.Coarser is { } coarser ? CoverOf(coarser, p) : own;
    }

    private Swap? SwapOf(TerrainLevel level, ChunkPosition p) => _pipeline?.Get(level.Key(p))?.Output<Swap>(_swap!);

    private float Faded(Swap swap)
        => FadeSeconds <= 0f ? 1f : (float)System.Math.Clamp((_time - swap.FadeAt) / FadeSeconds, 0.0, 1.0);

    /// <summary>The level of a coarse chunk, and the finer level under it, if they're still there.</summary>
    private TerrainLevel? LevelOf(ChunkKey key)
        => key.Grid < Levels.Count && Levels[key.Grid] is { Finer: not null } level && level.Grid.Id == key.Grid ? level : null;

    /// <summary>Covers a coarse chunk in the light store while the finer level has its eight there (or air): from
    /// before they're lit, so their first light is their own blocks', and while they're held, drawn as it swaps in over
    /// them. Its output is the chunk while it's covered; let go of, it's uncovered.</summary>
    private sealed class CoverStage(TerrainSwapSystem swaps) : FrameStage
    {
        public override string Name => "Cover";
        public override Purpose Serves => Purpose.Draw;
        public override bool AppliesTo(PipelineGrid grid) => grid.Level > 0;

        public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => new Pipeline.Input[]
        {
            new(swaps._store!, Relation.Self),
            new(swaps._store!, Relation.Children, AcceptsMissing: true),
        };

        public override StageResult Run(PipelineChunk record, StageInputs inputs)
        {
            if (swaps._pipeline!.EntryOf(record.Key) is not { } entry || swaps.LevelOf(record.Key) is not { } level)
                return StageResult.Done(); // air
            var pipeline = swaps._pipeline;
            for (int j = 0; j < 8; j++)
            {
                var p = Relation.Child(record.Key.Pos, j);
                if (pipeline.Get(level.Finer!.Key(p)) is { Wanted: true } child
                        ? !child.EverDone(swaps._store!)
                        : !swaps._load.Has(level.Finer, p))
                    return StageResult.Done();
            }
            swaps.SetCovered?.Invoke(entry.Volume.Gpu, entry.Position, true); // again if it went into the store again
            return StageResult.Done(entry);
        }

        public override void Release(object output)
        {
            var entry = (ChunkEntry)output;
            // Not if it's been made again since: the chunk there now is the one covered.
            if (entry.Volume.GetEntry(entry.Position) == entry) swaps.SetCovered?.Invoke(entry.Volume.Gpu, entry.Position, false);
        }
    }

    /// <summary>A coarse chunk's swap with the eight under it (see the class summary); its output is the
    /// <see cref="Swap"/>, the same one all along, which lets go of its holds when it's let go of.</summary>
    private sealed class SwapStage(TerrainSwapSystem swaps) : FrameStage
    {
        public override string Name => "Swap";
        public override Purpose Serves => Purpose.Draw;
        public override bool AppliesTo(PipelineGrid grid) => grid.Level > 0;

        public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => new Pipeline.Input[]
        {
            new(swaps._shown!, Relation.Self),
            new(swaps._shown!, Relation.Children, AcceptsMissing: true),
        };

        public override StageResult Run(PipelineChunk record, StageInputs inputs)
            => swaps._pipeline!.EntryOf(record.Key) is null || swaps.LevelOf(record.Key) is not { } level
                ? StageResult.Done() // air
                : swaps.Move(record, level);

        public override void Release(object output)
        {
            var swap = (Swap)output;
            swaps.Hold(swap, false);
        }
    }

    /// <summary>Moves a coarse chunk's swap on as its eight come, show or are let go of: see the class summary.</summary>
    private StageResult Move(PipelineChunk record, TerrainLevel level)
    {
        var finer = level.Finer!;
        // Ready: the finer level wants all eight, and has each shown (or air). Inside: it wants all eight.
        bool ready = true, inside = true;
        for (int j = 0; j < 8; j++)
        {
            var p = Relation.Child(record.Key.Pos, j);
            var child = _pipeline!.Get(finer.Key(p));
            if (child is { Asked: true }) ready &= child.EverDone(_shown!);
            else
            {
                ready &= child == null && _load.Has(finer, p);
                inside &= child == null && ChunkLoadSystem.Wanted(finer, p);
            }
        }
        var swap = record.Output<Swap>(_swap!);
        if (swap == null)
        {
            swap = new Swap(record.Key) { Drawn = !inside };
            if (swap.Drawn) swap.FadeAt = _time; // in over what's there
        }
        else if (swap.Drawn && ready)
        {
            if (!Lit(finer, record.Key.Pos)) return StageResult.Waiting; // looked at again next frame
            swap.Drawn = false;
            swap.FadeAt = _time;
        }
        else if (!swap.Drawn && !ready && !inside)
        {
            swap.Drawn = true;
            swap.FadeAt = _time;
        }
        bool fading = Faded(swap) < 1f;
        Hold(swap, !swap.Drawn || fading);
        // Coming back in: run again each frame till it's in, then it lets go of the eight.
        return swap.Drawn && fading && swap.Held != 0 ? StageResult.Again(swap) : StageResult.Done(swap);
    }

    /// <summary>Holds the eight under a swap that are shown (or lets go of those it holds).</summary>
    private void Hold(Swap swap, bool hold)
    {
        var finer = _pipeline!.Grid(swap.Key.Grid).Finer!;
        for (int j = 0; j < 8; j++)
        {
            int bit = 1 << j;
            if (((swap.Held & bit) != 0) == hold) continue;
            var key = finer.Key(Relation.Child(swap.Key.Pos, j));
            if (!hold) _pipeline.Hold(key, false);
            else if (_pipeline.Get(key) is not { } child || !child.EverDone(_shown!) || !_pipeline.Hold(key, true)) continue;
            swap.Held ^= (byte)bit;
        }
    }

    /// <summary>Whether the eight under coarse chunk <paramref name="p"/> are lit by their own blocks; those that
    /// aren't are lit first.</summary>
    private bool Lit(TerrainLevel finer, ChunkPosition p)
    {
        if (IsLit == null) return true;
        bool lit = true;
        for (int j = 0; j < 8; j++)
        {
            if (_pipeline!.EntryOf(finer.Key(Relation.Child(p, j))) is not { } entry || IsLit(entry.Volume.Gpu, entry.Position)) continue;
            ShowFirst?.Invoke(entry.Volume.Gpu, entry.Position);
            lit = false;
        }
        return lit;
    }

    // The panel's counts, per coarse level, worked out a few times a second (they walk every chunk in the pipeline).
    private (int Drawn, int Out, int Fading, int Covered, int Held)[] _counts = Array.Empty<(int, int, int, int, int)>();
    private readonly System.Diagnostics.Stopwatch _countsAge = new();

    /// <summary>The Distant Terrain panel's part: the fade, and each level's swaps.</summary>
    internal void DrawUi()
    {
        float fade = FadeSeconds;
        if (ImGui.SliderFloat("Swap fade (seconds)", ref fade, 0f, 4f, "%.2f")) FadeSeconds = fade;
        if (_pipeline == null) return;
        if (!_countsAge.IsRunning || _countsAge.Elapsed.TotalMilliseconds >= 250) Count();
        for (int k = 1; k < _counts.Length && k < Levels.Count; k++)
        {
            var (drawn, swappedOut, fading, covered, held) = _counts[k];
            ImGui.Text($"Level {k}: {drawn:N0} drawn, {swappedOut:N0} swapped out ({held:N0} finer chunks held), {fading:N0} fading, " +
                       $"{covered:N0} covered");
        }
    }

    private void Count()
    {
        _countsAge.Restart();
        _counts = new (int, int, int, int, int)[Levels.Count];
        foreach (var record in _pipeline!.Records)
        {
            if (record.Grid.Level == 0 || record.Key.Grid >= _counts.Length) continue;
            ref var c = ref _counts[record.Key.Grid];
            if (record.Output<ChunkEntry>(_cover!) != null) c.Covered++;
            if (record.Output<Swap>(_swap!) is not { } swap) continue;
            if (swap.Drawn) c.Drawn++;
            else c.Out++;
            if (Faded(swap) < 1f) c.Fading++;
            c.Held += System.Numerics.BitOperations.PopCount(swap.Held);
        }
    }
}
