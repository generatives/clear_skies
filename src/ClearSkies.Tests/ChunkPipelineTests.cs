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
    private sealed class Reader(string name, Func<PipelineGrid, Input[]> inputs, Func<PipelineGrid, bool>? applies = null,
                                Purpose serves = Purpose.None) : WorkerStage<string>
    {
        public int Runs;
        public readonly List<string> Released = new();
        public override string Name => name;
        public override Purpose Serves => serves;
        public override void Release(object output) => Released.Add((string)output);
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
    public void A_grid_added_after_chunks_gets_its_stages_and_one_removed_lets_go_of_its_chunks()
    {
        var pipeline = new ChunkPipeline(work => work());
        var world = pipeline.AddGrid("world");
        var gen = pipeline.Add(new Gen(grid => grid.Name != "ship"));
        var mesh = pipeline.Add(new Reader("mesh", _ => new[] { new Input(gen, Relation.Self) }, grid => grid.Name != "ship"));
        pipeline.Want(world.Key(P(0)), true);
        pipeline.Update(Plenty);

        var ship = pipeline.AddGrid("ship");
        var other = pipeline.AddGrid("other");
        pipeline.Want(ship.Key(P(0)), true);
        pipeline.Want(other.Key(P(0)), true);
        for (int i = 0; i < 3; i++) pipeline.Update(Plenty);
        Assert.Equal(SlotState.Done, pipeline.Get(other.Key(P(0)))!.State(mesh));
        Assert.Equal(2, gen.Runs); // not on the ship's chunk

        pipeline.RemoveGrid(other);
        pipeline.Update(Plenty);
        Assert.Null(pipeline.Get(other.Key(P(0))));
        Assert.Single(gen.Released, r => r.StartsWith($"gen {other.Id}:"));
    }

    [Fact]
    public void A_stage_serving_a_purpose_runs_only_while_its_chunk_is_wanted_for_it()
    {
        var jobs = new Queue<Action>();
        var pipeline = new ChunkPipeline(jobs.Enqueue);
        var grid = pipeline.AddGrid("world");
        var gen = pipeline.Add(new Gen());
        var mesh = pipeline.Add(new Reader("mesh", _ => new[] { new Input(gen, Relation.Self) }, serves: Purpose.Draw));
        var near = pipeline.Add(new Reader("near", _ => new[] { new Input(mesh, Relation.Neighbours, AcceptsMissing: true) }));
        var key = grid.Key(P(0));
        void Settle() { for (int i = 0; i < 4; i++) { pipeline.Update(Plenty); while (jobs.TryDequeue(out var job)) job(); } }

        pipeline.Want(key, true);
        pipeline.Want(grid.Key(P(1)), true);
        Settle();
        Assert.Equal(0, mesh.Runs);
        Assert.False(pipeline.Get(key)!.Needs(mesh));

        pipeline.WantFor(key, Purpose.Draw, true);
        Assert.True(pipeline.Get(key)!.Needs(mesh));
        Settle();
        Assert.Equal(1, mesh.Runs);
        Assert.Equal(key.ToString(), pipeline.Get(key)!.Output<string>(mesh));
        Assert.StartsWith($"{key} ", pipeline.Get(grid.Key(P(1)))!.Output<string>(near)); // its other neighbours aren't wanted

        // No longer drawn: what it made is let go of, and what read it sees it missing.
        pipeline.WantFor(key, Purpose.Draw, false);
        Assert.Equal(new[] { key.ToString() }, mesh.Released);
        Assert.Null(pipeline.Get(key)!.Output<string>(mesh));
        Settle();
        Assert.StartsWith($"-{key} ", pipeline.Get(grid.Key(P(1)))!.Output<string>(near));
        Assert.Equal(1, mesh.Runs);

        // Let go of while it's being made: what it makes is let go of too.
        pipeline.WantFor(key, Purpose.Draw, true);
        pipeline.Update(Plenty);
        pipeline.WantFor(key, Purpose.Draw, false);
        while (jobs.TryDequeue(out var job)) job();
        pipeline.Update(Plenty);
        Assert.Equal(2, mesh.Released.Count);
        Assert.Null(pipeline.Get(key)!.Output<string>(mesh));
        Assert.Equal(SlotState.Waiting, pipeline.Get(key)!.State(mesh));
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
    public void An_urgent_change_goes_before_queued_work_and_passes_on_through_the_stages_after()
    {
        var jobs = new Queue<Action>();
        var urgent = new Queue<Action>();
        var pipeline = new ChunkPipeline((_, work) => jobs.Enqueue(work), 1, urgent.Enqueue);
        var grid = pipeline.AddGrid("world");
        var gen = pipeline.Add(new Gen());
        var mesh = pipeline.Add(new Reader("mesh", _ => new[] { new Input(gen, Relation.Self) }));
        var upload = pipeline.Add(new Reader("upload", _ => new[] { new Input(mesh, Relation.Self) }));
        var edited = grid.Key(P(0));
        pipeline.Want(edited, true);
        for (int i = 0; i < 3; i++) { pipeline.Update(Plenty); while (jobs.TryDequeue(out var job)) job(); }
        pipeline.Update(Plenty);
        Assert.Equal(1, upload.Runs);

        var oneSlot = new FrameBudgets(WorkerSlots: 1, MainMs: 1000);
        for (int x = 1; x <= 3; x++) pipeline.Want(grid.Key(P(x)), true);
        pipeline.Update(oneSlot);
        Assert.Single(jobs);                        // the worker is full

        pipeline.OutputChanged(edited, gen, new Change(0b111111, Urgent: true));
        pipeline.Update(oneSlot);
        Assert.Single(jobs);
        Assert.Single(urgent);                      // the mesh runs anyway, ahead of what's queued
        jobs.Dequeue()();
        urgent.Dequeue()();
        pipeline.Update(oneSlot);                   // the urgent mesh is taken first, so its upload goes first too
        Assert.Single(urgent);
        Assert.Single(jobs);                        // and the next far chunk took the worker's slot
        urgent.Dequeue()();
        pipeline.Update(oneSlot);
        Assert.Equal(2, upload.Runs);
        Assert.Empty(urgent);
    }

    [Fact]
    public void The_nearest_chunk_goes_first_when_workers_are_short()
    {
        var jobs = new Queue<Action>();
        var pipeline = new ChunkPipeline(jobs.Enqueue);
        var grid = pipeline.AddGrid("world");
        var gen = pipeline.Add(new Gen());
        pipeline.Priority = (key, _) => Math.Abs(key.Pos.X);
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
