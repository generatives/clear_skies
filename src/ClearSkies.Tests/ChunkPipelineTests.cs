using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>The chunk pipeline's scheduling, with stages that only record what they read.</summary>
public class ChunkPipelineTests
{
    private static readonly FrameBudgets Plenty = new(WorkerSlots: 64, MainMs: 1000);

    /// <summary>Makes a chunk's "voxels": a name and a version, counting its runs.</summary>
    private sealed class Gen(Func<PipelineGrid, bool>? applies = null) : WorkerStage<string>
    {
        public int Runs;
        public override bool AppliesTo(PipelineGrid grid) => applies?.Invoke(grid) ?? true;
        public readonly List<string> Released = new();
        public override IReadOnlyList<Input> InputsFor(PipelineGrid grid) => Array.Empty<Input>();
        public override string Make(ChunkKey key, StageInputs inputs) { Interlocked.Increment(ref Runs); return $"gen {key}"; }
        public override void Release(object output) => Released.Add((string)output);
    }

    /// <summary>Reads stage <c>of</c> through the given inputs; its output lists what it read (- for missing).</summary>
    private sealed class Reader(string name, Func<PipelineGrid, Input[]> inputs, Func<PipelineGrid, bool>? applies = null) : WorkerStage<string>
    {
        public int Runs;
        public override string Name => name;
        public override bool AppliesTo(PipelineGrid grid) => applies?.Invoke(grid) ?? true;
        public override IReadOnlyList<Input> InputsFor(PipelineGrid grid) => inputs(grid);
        public override string Make(ChunkKey key, StageInputs read)
        {
            Interlocked.Increment(ref Runs);
            var parts = new List<string>();
            foreach (var input in inputs(null!))
                foreach (var (k, o) in read.All<string>(input.Stage)) parts.Add(o == null ? $"-{k}" : k.ToString());
            return string.Join(" ", parts.Distinct());
        }
    }

    private sealed class Asked : ExternalStage
    {
        public int Requests;
        public bool Lit;
        private readonly Stage _after;
        public Asked(Stage after) => _after = after;
        public override IReadOnlyList<Input> InputsFor(PipelineGrid grid) => new[] { new Input(_after, Relation.Self) };
        public override StageResult Request(PipelineChunk record)
        {
            Requests++;
            return Lit ? StageResult.Done() : StageResult.Waiting;
        }
    }

    private static ChunkPosition P(int x, int y = 0, int z = 0) => new(x, y, z);

    [Fact]
    public void A_stage_runs_once_its_input_is_done()
    {
        var pipeline = new ChunkPipeline(work => work());
        var grid = pipeline.AddGrid("world");
        var gen = pipeline.Add(new Gen());
        var mesh = pipeline.Add(new Reader("mesh", _ => new[] { new Input(gen, Relation.Self) }));
        var key = grid.Key(P(0));

        pipeline.Want(key, true);
        pipeline.Update(Plenty);
        Assert.Equal(1, gen.Runs);
        Assert.Equal(0, mesh.Runs); // gen's result is taken next update, then mesh is ready
        pipeline.Update(Plenty);
        pipeline.Update(Plenty);

        var record = pipeline.Get(key)!;
        Assert.Equal(SlotState.Done, record.State(mesh));
        Assert.Equal(key.ToString(), record.Output<string>(mesh));
    }

    [Fact]
    public void A_stage_reading_other_chunks_waits_for_them_to_be_wanted_and_never_loads_them()
    {
        var pipeline = new ChunkPipeline(work => work());
        var fine = pipeline.AddGrid("full detail");
        var coarse = pipeline.AddGrid("level 1", finer: fine);
        var gen = pipeline.Add(new Gen(g => g.Finer == null));
        var shrink = pipeline.Add(new Reader("shrink", _ => new[] { new Input(gen, Relation.Children) }, g => g.Finer != null));
        var parent = coarse.Key(P(0));

        pipeline.Want(parent, true);
        for (int i = 0; i < 3; i++) pipeline.Update(Plenty);
        Assert.Equal(0, gen.Runs);
        Assert.Equal(0, shrink.Runs);
        Assert.Equal(1, pipeline.ChunkCount);

        for (int j = 0; j < 8; j++) pipeline.Want(fine.Key(Relation.Child(P(0), j)), true);
        for (int i = 0; i < 3; i++) pipeline.Update(Plenty);
        Assert.Equal(8, gen.Runs);
        Assert.Equal(1, shrink.Runs);
    }

    [Fact]
    public void A_stage_that_accepts_missing_neighbours_runs_without_them_and_again_when_they_come()
    {
        var pipeline = new ChunkPipeline(work => work());
        var grid = pipeline.AddGrid("world");
        var gen = pipeline.Add(new Gen());
        var mesh = pipeline.Add(new Reader("mesh", _ => new[]
        {
            new Input(gen, Relation.Self), new Input(gen, Relation.Neighbour(1), AcceptsMissing: true),
        }));
        ChunkKey a = grid.Key(P(0)), b = grid.Key(P(1));

        pipeline.Want(a, true);
        for (int i = 0; i < 3; i++) pipeline.Update(Plenty);
        Assert.Equal($"{a} -{b}", pipeline.Get(a)!.Output<string>(mesh));
        Assert.Null(pipeline.Get(b)); // never loaded for it

        pipeline.Want(b, true);
        for (int i = 0; i < 3; i++) pipeline.Update(Plenty);
        Assert.Equal($"{a} {b}", pipeline.Get(a)!.Output<string>(mesh));
    }

    [Fact]
    public void An_edit_reruns_a_neighbour_only_when_it_touched_their_shared_face()
    {
        var pipeline = new ChunkPipeline(work => work());
        var grid = pipeline.AddGrid("world");
        var gen = pipeline.Add(new Gen());
        var mesh = pipeline.Add(new Reader("mesh", _ => new[]
        {
            new Input(gen, Relation.Self), new Input(gen, Relation.ChangedBorder, AcceptsMissing: true),
        }));
        ChunkKey a = grid.Key(P(0)), b = grid.Key(P(1));
        pipeline.Want(a, true);
        pipeline.Want(b, true);
        for (int i = 0; i < 3; i++) pipeline.Update(Plenty);
        int before = mesh.Runs;

        pipeline.OutputChanged(b, gen, new Change(1 << 3)); // b's top face: a doesn't touch it
        for (int i = 0; i < 2; i++) pipeline.Update(Plenty);
        Assert.Equal(before + 1, mesh.Runs); // only b itself

        pipeline.OutputChanged(b, gen, new Change(1 << 0)); // b's -x face, shared with a
        for (int i = 0; i < 2; i++) pipeline.Update(Plenty);
        Assert.Equal(before + 3, mesh.Runs); // b and a
    }

    [Fact]
    public void A_change_while_running_keeps_the_old_output_then_runs_again()
    {
        var jobs = new Queue<Action>();
        var pipeline = new ChunkPipeline(jobs.Enqueue);
        var grid = pipeline.AddGrid("world");
        var gen = pipeline.Add(new Gen());
        var mesh = pipeline.Add(new Reader("mesh", _ => new[] { new Input(gen, Relation.Self) }));
        var key = grid.Key(P(0));
        pipeline.Want(key, true);
        pipeline.Update(Plenty);
        jobs.Dequeue()();
        pipeline.Update(Plenty);              // gen taken; mesh started
        jobs.Dequeue()();
        pipeline.Update(Plenty);              // mesh taken
        Assert.Equal(1, mesh.Runs);

        pipeline.OutputChanged(key, gen, Change.All);
        pipeline.Update(Plenty);              // mesh starts again
        pipeline.OutputChanged(key, gen, Change.All); // and its input changes while it runs
        Assert.Equal(SlotState.Running, pipeline.Get(key)!.State(mesh));
        Assert.True(pipeline.Get(key)!.EverDone(mesh)); // the old mesh is still in use
        jobs.Dequeue()();
        pipeline.Update(Plenty);              // taken, then started once more
        Assert.Single(jobs);
        jobs.Dequeue()();
        pipeline.Update(Plenty);
        Assert.Equal(3, mesh.Runs);
        Assert.Equal(SlotState.Done, pipeline.Get(key)!.State(mesh));
    }

    [Fact]
    public void A_chunk_no_longer_wanted_is_dropped_after_its_worker_finishes()
    {
        var jobs = new Queue<Action>();
        var pipeline = new ChunkPipeline(jobs.Enqueue);
        var grid = pipeline.AddGrid("world");
        var gen = pipeline.Add(new Gen());
        var key = grid.Key(P(0));
        pipeline.Want(key, true);
        pipeline.Update(Plenty);
        pipeline.Want(key, false);
        pipeline.Update(Plenty);
        Assert.NotNull(pipeline.Get(key)); // its worker is still making it

        jobs.Dequeue()();
        pipeline.Update(Plenty);
        Assert.Null(pipeline.Get(key));
        Assert.Single(gen.Released);
    }

    [Fact]
    public void The_nearest_chunk_goes_first_when_workers_are_short()
    {
        var jobs = new Queue<Action>();
        var pipeline = new ChunkPipeline(jobs.Enqueue);
        var grid = pipeline.AddGrid("world");
        var gen = pipeline.Add(new Gen());
        pipeline.Priority = key => Math.Abs(key.Pos.X);
        pipeline.Want(grid.Key(P(5)), true);
        pipeline.Want(grid.Key(P(1)), true);
        pipeline.Want(grid.Key(P(3)), true);

        pipeline.Update(new FrameBudgets(WorkerSlots: 1, MainMs: 1000));
        Assert.Single(jobs);
        jobs.Dequeue()();
        pipeline.Update(new FrameBudgets(WorkerSlots: 0, MainMs: 1000));
        Assert.True(pipeline.Get(grid.Key(P(1)))!.EverDone(gen));
        Assert.False(pipeline.Get(grid.Key(P(3)))!.EverDone(gen));
    }

    [Fact]
    public void An_external_stage_is_asked_every_frame_until_it_answers_done()
    {
        var pipeline = new ChunkPipeline(work => work());
        var grid = pipeline.AddGrid("world");
        var gen = pipeline.Add(new Gen());
        var light = pipeline.Add(new Asked(gen));
        var key = grid.Key(P(0));
        pipeline.Want(key, true);
        for (int i = 0; i < 4; i++) pipeline.Update(Plenty);
        Assert.Equal(SlotState.Running, pipeline.Get(key)!.State(light));
        int asked = light.Requests;
        Assert.True(asked >= 2);

        light.Lit = true;
        pipeline.Update(Plenty);
        Assert.Equal(SlotState.Done, pipeline.Get(key)!.State(light));
        Assert.Equal(asked + 1, light.Requests);
        pipeline.Update(Plenty);
        Assert.Equal(asked + 1, light.Requests); // not asked again
    }
}
