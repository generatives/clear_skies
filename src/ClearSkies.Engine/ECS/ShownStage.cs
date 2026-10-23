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
/// <item>Under a coarser chunk drawn over it, it's left out; it comes in as that swaps out, from the same time, so each
/// pixel shows one of them.</item>
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
        if (first && over == null && !AboveShown(record)) return StageResult.Waiting;
        showing ??= new Showing(record.Key);
        var entry = showing.Entry = pipeline.EntryOf(record.Key);
        bool wasOver = showing.Over, poll = false;
        if (record.Grid.Finer is { } finer && entry != null) poll = Swap(record, showing, finer, first, now);

        if (entry != null)
        {
            bool visible = over is not { Over: true } && (record.Grid.Finer == null || showing.Over);
            // From when the chunk over it swapped, while that's still fading, so the two dissolve into each other.
            double at = over != null && now - over.SwappedAt < FadeSeconds ? over.SwappedAt : now;
            var e = entry.Entity;
            if (!e.Has<Shown>()) e.Set(new Shown { Visible = visible, At = visible ? at : double.NegativeInfinity });
            else if (e.Get<Shown>().Visible != visible) e.Set(new Shown { Visible = visible, At = at });
        }

        // What's read of it (whether it's there, and drawn over the chunks under it) changed: they look again.
        bool changed = first || showing.Over != wasOver;
        if (!poll) return changed ? StageResult.Done(showing) : StageResult.Same(showing);
        if (!changed) return StageResult.Waiting;
        pipeline.Invalidate(record.Key, this); // asked again once this is taken
        return StageResult.Done(showing);
    }

    /// <summary>Whether a coarse chunk is drawn over the eight under it (rather than them); false before it's shown.</summary>
    internal bool DrawnOver(PipelineChunk record) => record.Output<Showing>(this) is { Over: true };

    public override void Release(object output)
    {
        var showing = (Showing)output;
        if (showing.Held != 0) Hold(showing, pipeline.Grid(showing.Key.Grid).Finer!, false);
        if (showing.Entry?.Entity is { IsAlive: true } e && e.Has<Shown>()) e.Remove<Shown>();
    }

    /// <summary>The coarser chunk's showing over this one, if it's there and shown (not air).</summary>
    private Showing? Over(PipelineChunk record)
        => record.Grid.Coarser is { } coarser
           && pipeline.Get(coarser.Key(Relation.Over(record.Key.Pos))) is { Wanted: true } parent
           && parent.Output<Showing>(this) is { Entry: not null } showing
            ? showing
            : null;

    private bool AboveShown(PipelineChunk record)
    {
        var p = record.Key.Pos;
        return pipeline.Get(record.Key with { Pos = new ChunkPosition(p.X, p.Y + 1, p.Z) }) is not { Wanted: true } above
               || !above.Needs(this) || above.EverDone(this);
    }

    /// <summary>Moves a coarse chunk's swap with the eight under it on (see the class summary); returns whether to look
    /// again next frame: for the eight's light, or till it's in over them and can let go of them.</summary>
    private bool Swap(PipelineChunk record, Showing showing, PipelineGrid finer, bool first, double now)
    {
        // Ready: the finer level wants all eight, and has each shown (or air). Inside: it wants all eight.
        bool ready = true, inside = true;
        for (int j = 0; j < 8; j++)
        {
            var key = finer.Key(Relation.Child(record.Key.Pos, j));
            var child = pipeline.Get(key);
            if (child is { Asked: true }) ready &= child.EverDone(this);
            else
            {
                ready &= child == null && terrain?.Has(key) == true;
                inside &= child == null && terrain?.InRange(key) == true;
            }
        }
        bool poll = false;
        if (first) (showing.Over, showing.SwappedAt) = (!inside, inside ? double.NegativeInfinity : now);
        else if (showing.Over && ready)
        {
            if (Lit(finer, record.Key.Pos)) (showing.Over, showing.SwappedAt) = (false, now);
            else poll = true;
        }
        else if (!showing.Over && !ready && !inside) (showing.Over, showing.SwappedAt) = (true, now);
        bool fading = now - showing.SwappedAt < FadeSeconds;
        Hold(showing, finer, !showing.Over || fading);
        return poll || showing.Over && fading && showing.Held != 0;
    }

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
            stored &= child is { Wanted: true } ? child.EverDone(store) : terrain?.Has(key) == true;
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
