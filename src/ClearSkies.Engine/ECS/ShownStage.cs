using ClearSkies.Engine.Core;
using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>Whether a chunk being drawn is seen (see <see cref="ShownStage"/>): it's left out without this. It dissolves
/// in over <see cref="ShownStage.FadeSeconds"/> from <see cref="At"/> while <see cref="Visible"/>, and out from it once
/// not (see <see cref="ChunkRenderSystem"/>).</summary>
public struct Shown
{
    /// <summary>When it last came into view or went out of it (<see cref="Time.TotalSeconds"/>).</summary>
    public double At;
    public bool Visible;

    /// <summary>How it's drawn at <paramref name="now"/>: 0 whole; d between 0 and 1 leaves out that share of its pixels
    /// in an even screen dither (1: not at all); -d draws only the pixels d leaves out. So of two chunks in the same
    /// place, one going out and one coming in from the same time, each pixel shows one.</summary>
    public readonly float Dissolve(double now)
    {
        float fade = ShownStage.FadeSeconds;
        float left = fade <= 0f ? 0f : 1f - (float)System.Math.Clamp((now - At) / fade, 0.0, 1.0);
        return Visible ? left : left <= 0f ? 1f : -left;
    }
}

/// <summary>
/// Shows a chunk once it's lit and baked (<see cref="Shown"/>), dissolving in, and swaps the levels of detail the chunk
/// loader streams (see <see cref="ChunkLoadSystem.SetLevels"/>), each chunk deciding as the levels around it change:
/// <list type="bullet">
/// <item>Under a coarser chunk drawn over it, at any level up, it's left out; it comes in as that swaps out, from the
/// same time, so each pixel shows one of them.</item>
/// <item>A coarse chunk is drawn over the eight finer chunks under it until they're all shown (or air) and lit, then
/// swaps out for them; once the finer level lets go of them, it swaps back in. It holds them in the pipeline
/// (<see cref="ChunkPipeline.Hold"/>) while it's out, and till it's in, so they don't vanish first. One first shown
/// where the finer level wants all eight isn't drawn: it would cover finer chunks already shown, and be lit in place of
/// them, till the rest come, which they soon do.</item>
/// <item>Under none, it's shown once the chunk above it is (if that's wanted): underground chunks loaded before the
/// surface showed through the gap, dimly lit.</item>
/// </list>
/// It's drawn with its last mesh and light while it's made again (after an edit, or a neighbour coming). Not drawn any
/// more, it's let go of, and no longer shown.
/// </summary>
public sealed class ShownStage(ChunkPipeline pipeline, Time time, Stage ready, ChunkLoadSystem? terrain) : ExternalStage
{
    /// <summary>How long a chunk takes to dissolve in or out (seconds; 0: at once).</summary>
    public static float FadeSeconds { get; set; } = 2f;

    /// <summary>Whether a chunk of a grid is lit (GpuLightSystem.IsLit): a coarse chunk swaps out only once the finer
    /// chunks under it are lit by their own blocks. Without it, everything counts as lit.</summary>
    public Func<GridHandle, ChunkPosition, bool>? IsLit { get; init; }

    /// <summary>Puts a chunk of a grid ahead of the light queues (GpuLightSystem.ShowFirst): those a swap waits for.</summary>
    public Action<GridHandle, ChunkPosition>? ShowFirst { get; init; }

    /// <summary>A chunk's place in being shown: its output, the same one all along, which lets go of its holds when it's
    /// let go of.</summary>
    private sealed class Showing(ChunkKey key)
    {
        public readonly ChunkKey Key = key;
        public ChunkEntry? Entry; // null: air
        public bool Over;         // a coarse chunk drawn over the eight under it, rather than them
        public double SwappedAt = double.NegativeInfinity;
        public bool Hidden;       // under a coarser chunk drawn over it
        // Whether what's under it is left out (it, or one over it, is drawn over them), and since when.
        public bool HidesBelow => Over || Hidden;
        public double BelowAt = double.NegativeInfinity;
        public byte Held;         // which of the eight it holds, a bit each (see Relation.Child)
    }

    public override string Name => "Shown";
    public override Purpose Serves => Purpose.Draw;

    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => new Pipeline.Input[]
    {
        new(ready, Relation.Self),
        new(this, Relation.Above, AcceptsMissing: true),
        new(this, Relation.Parent, AcceptsMissing: true),
        new(this, Relation.Children, AcceptsMissing: true),
    };

    public override StageResult Request(PipelineChunk record)
    {
        double now = time.TotalSeconds;
        var showing = record.Output<Showing>(this);
        bool first = showing == null;
        var over = Over(record);
        if (first && over == null && !AboveShown(record)) return StageResult.WaitingForInputs;
        showing ??= new Showing(record.Key);
        var entry = showing.Entry = pipeline.EntryOf(record.Key);
        bool wasOver = showing.Over, wasBelow = showing.HidesBelow;
        double again = record.Grid.Finer is { } finer && entry != null ? Swap(record, showing, finer, first, now) : 0;
        Show(record.Grid, showing, over, wasBelow, showing.Over != wasOver, now);

        var result = first ? StageResult.Done(showing)
                   : showing.Over != wasOver || showing.HidesBelow != wasBelow ? StageResult.Done(showing)
                   : StageResult.Same(showing);
        return again > 0 ? result.AskAgainIn(again) : result;
    }

    /// <summary>Sets whether a chunk is seen, from the chunk over it (<paramref name="over"/>); then, if whether it hides
    /// the chunks under it changed (<paramref name="wasBelow"/>), theirs, all the way down, at once: a swap dissolves
    /// both levels from the same frame. (Left to their own turns, which can be frames later while loading, both would
    /// be drawn whole for a while, z-fighting, or neither, a gap.)</summary>
    private void Show(PipelineGrid grid, Showing showing, Showing? over, bool wasBelow, bool swapped, double now)
    {
        showing.Hidden = over is { HidesBelow: true };
        if (showing.HidesBelow != wasBelow) showing.BelowAt = swapped ? showing.SwappedAt : over?.BelowAt ?? now;
        if (showing.Entry?.Entity is { IsAlive: true } e)
        {
            bool visible = !showing.Hidden && (grid.Finer == null || showing.Over);
            // From when what's over it swapped, while that's still fading, so the two dissolve into each other.
            double at = over != null && now - over.BelowAt < FadeSeconds ? over.BelowAt : now;
            if (!e.Has<Shown>()) e.Set(new Shown { Visible = visible, At = visible ? at : double.NegativeInfinity });
            else if (e.Get<Shown>().Visible != visible) e.Set(new Shown { Visible = visible, At = at });
        }

        if (showing.HidesBelow != wasBelow && grid.Finer is { } finer) ShowUnder(finer, showing.Key.Pos, showing, now);
    }

    /// <summary>Sets whether the chunks under coarse chunk <paramref name="p"/> are seen, now that whether
    /// <paramref name="over"/> hides them changed: past those not shown yet (or air) to the ones under them.</summary>
    private void ShowUnder(PipelineGrid finer, ChunkPosition p, Showing over, double now)
    {
        for (int j = 0; j < 8; j++)
        {
            var key = finer.Key(Relation.Child(p, j));
            if (pipeline.Get(key) is not { Wanted: true } child) continue;
            if (child.Output<Showing>(this) is { } under) Show(finer, under, over, under.HidesBelow, false, now);
            else if (finer.Finer is { } finest) ShowUnder(finest, key.Pos, over, now);
        }
    }

    /// <summary>Whether a coarse chunk is drawn over the eight under it (rather than them); false before it's shown.</summary>
    internal bool DrawnOver(PipelineChunk record) => record.Output<Showing>(this) is { Over: true };

    public override void Release(object output)
    {
        var showing = (Showing)output;
        if (showing.Held != 0) Hold(showing, pipeline.Grid(showing.Key.Grid).Finer!, false);
        if (showing.Entry?.Entity is { IsAlive: true } e && e.Has<Shown>()) e.Remove<Shown>();
    }

    /// <summary>The nearest coarser chunk's showing over this one that's shown (not air), past those not shown yet:
    /// a chunk under one that isn't, under one drawn, is hidden like the one under it. Null if there's none.</summary>
    private Showing? Over(PipelineChunk record)
    {
        var p = record.Key.Pos;
        for (var coarser = record.Grid.Coarser; coarser != null; coarser = coarser.Coarser)
        {
            p = Relation.Over(p);
            if (pipeline.Get(coarser.Key(p)) is not { Wanted: true } over) return null;
            if (over.Output<Showing>(this) is { Entry: not null } showing) return showing;
        }
        return null;
    }

    private bool AboveShown(PipelineChunk record)
    {
        var p = record.Key.Pos;
        return pipeline.Get(record.Key with { Pos = new ChunkPosition(p.X, p.Y + 1, p.Z) }) is not { Wanted: true } above
               || !above.Needs(this) || above.EverDone(this);
    }

    /// <summary>How often a coarse chunk ready to swap out looks to see if the eight under it are lit (seconds).</summary>
    private const double LitEvery = 0.1;

    /// <summary>Moves a coarse chunk's swap with the eight under it on (see the class summary); returns when to look
    /// again (seconds from now; 0: once they change): for the eight's light, or when it's in over them and can let go
    /// of them.</summary>
    private double Swap(PipelineChunk record, Showing showing, PipelineGrid finer, bool first, double now)
    {
        // Ready: the finer level wants all eight, and has each shown (or air, never shown). Inside: it wants all eight.
        bool ready = true, inside = true;
        for (int j = 0; j < 8; j++)
        {
            var key = finer.Key(Relation.Child(record.Key.Pos, j));
            var child = pipeline.Get(key);
            ready &= child is null or { Asked: true } && (child?.EverDone(this) == true || IsAir(key));
            inside &= child is { Asked: true } || child == null && terrain?.InRange(key) == true;
        }
        bool waitingForLight = false;
        if (first) (showing.Over, showing.SwappedAt) = (!inside, inside ? double.NegativeInfinity : now);
        else if (showing.Over && ready)
        {
            if (Lit(finer, record.Key.Pos)) (showing.Over, showing.SwappedAt) = (false, now);
            else waitingForLight = true;
        }
        else if (!showing.Over && !ready && !inside) (showing.Over, showing.SwappedAt) = (true, now);
        bool fading = now - showing.SwappedAt < FadeSeconds;
        Hold(showing, finer, !showing.Over || fading);
        return waitingForLight ? LitEvery : showing.Over && fading && showing.Held != 0 ? showing.SwappedAt + FadeSeconds - now : 0;
    }

    /// <summary>Whether the level has the chunk and it's air (not drawn, so never shown): made so, or known to be.</summary>
    internal bool IsAir(ChunkKey key) => pipeline.EntryOf(key) == null && terrain?.Has(key) == true;

    /// <summary>Holds the eight under a coarse chunk that are shown (or lets go of those it holds).</summary>
    private void Hold(Showing showing, PipelineGrid finer, bool hold)
    {
        for (int j = 0; j < 8; j++)
        {
            int bit = 1 << j;
            if (((showing.Held & bit) != 0) == hold) continue;
            var key = finer.Key(Relation.Child(showing.Key.Pos, j));
            if (!hold) pipeline.Hold(key, false);
            else if (pipeline.Get(key) is not { } child || !child.EverDone(this) || !pipeline.Hold(key, true)) continue;
            showing.Held ^= (byte)bit;
        }
    }

    /// <summary>Whether the eight under coarse chunk <paramref name="p"/> are lit by their own blocks; those that aren't
    /// are lit first.</summary>
    private bool Lit(PipelineGrid finer, ChunkPosition p)
    {
        if (IsLit == null) return true;
        bool lit = true;
        for (int j = 0; j < 8; j++)
        {
            if (pipeline.EntryOf(finer.Key(Relation.Child(p, j))) is not { } entry || IsLit(entry.Volume.Gpu, entry.Position)) continue;
            ShowFirst?.Invoke(entry.Volume.Gpu, entry.Position);
            lit = false;
        }
        return lit;
    }
}

/// <summary>Covers a coarse chunk in the light store (<see cref="GridStore.SetCovered"/>) while the finer level has the
/// eight under it there (or air): the finer grid's rays pass through it, so each level is lit by its own blocks. From
/// before they're lit, so their first light is their own blocks', and while they're held, drawn as it swaps in over them
/// (see <see cref="ShownStage"/>). One not drawn over them is covered from when the finer level wants all eight, so the
/// first of them in aren't lit in its shade (it isn't drawn till they're gone, see ShownStage). Its output is the chunk
/// while it's covered; let go of, it's uncovered.</summary>
internal sealed class CoverStage(ChunkPipeline pipeline, Stage store, ShownStage shown, ChunkLoadSystem? terrain,
                                 Action<ChunkEntry, bool>? setCovered) : FrameStage
{
    public override string Name => "Cover";
    public override Purpose Serves => Purpose.Draw;
    public override bool AppliesTo(PipelineGrid grid) => grid.Finer != null;

    public override IReadOnlyList<Pipeline.Input> InputsFor(PipelineGrid grid) => new Pipeline.Input[]
    {
        new(store, Relation.Self),
        new(store, Relation.Children, AcceptsMissing: true),
        new(shown, Relation.Self, AcceptsMissing: true),
    };

    public override StageResult Run(PipelineChunk record, StageInputs inputs)
    {
        if (pipeline.EntryOf(record.Key) is not { } entry) return StageResult.Done(); // air
        var finer = record.Grid.Finer!;
        bool stored = true, wanted = true;
        for (int j = 0; j < 8; j++)
        {
            var key = finer.Key(Relation.Child(record.Key.Pos, j));
            var child = pipeline.Get(key);
            stored &= child is { Wanted: true } && child.EverDone(store) || shown.IsAir(key);
            wanted &= child is { Asked: true } || child == null && terrain?.InRange(key) == true;
        }
        if (!stored && !(wanted && !shown.DrawnOver(record))) return StageResult.Done();
        setCovered?.Invoke(entry, true); // again if it went into the store again
        return StageResult.Done(entry);
    }

    public override void Release(object output)
    {
        var entry = (ChunkEntry)output;
        // Not if it's been made again since: the chunk there now is the one covered.
        if (entry.Volume.GetEntry(entry.Position) == entry) setCovered?.Invoke(entry, false);
    }
}
