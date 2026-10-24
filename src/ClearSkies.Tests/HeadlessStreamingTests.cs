using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Edited chunks straight from a save, for streaming with no Host: always there to load.</summary>
internal sealed class SavedChunkStore(SaveDatabase db) : IChunkStore
{
    public IEnumerable<ChunkPosition> EditedChunks() => db.ChunkPositions();
    public bool IsReady(ChunkPosition pos) => true;
    public void Request(ChunkPosition pos) { }
    public void Outdated(ChunkPosition pos) { }

    public bool TryLoad(ChunkPosition pos, ChunkData data)
    {
        if (db.ReadChunk(pos) is not { } blob) return false;
        StaticWorldSerializer.Read(blob, data);
        return true;
    }

    public void Save(ChunkPosition pos, ChunkData data) => db.WriteChunk(pos, StaticWorldSerializer.ToBytes(data));
}

/// <summary>Terrain streaming with nothing drawn (headless): limited by a count of chunks.</summary>
public class HeadlessStreamingTests
{
    /// <summary>Stone below y = 0, air above.</summary>
    internal sealed class Flat : IWorldGenerator
    {
        public void Generate(ChunkData data, ChunkPosition pos)
        {
            if (pos.Y >= 0) return;
            for (int z = 0; z < ChunkData.Size; z++) for (int y = 0; y < ChunkData.Size; y++) for (int x = 0; x < ChunkData.Size; x++)
                data.Set(x, y, z, BlockId.Stone);
        }

        /// <summary>The stone: the 8 layers streamed below y = 0 (bit 0 is layer minChunkY - 8).</summary>
        public ulong ColumnLayers(int chunkX, int chunkZ, int minChunkY) => 0xFF;
    }

    internal static DefaultEcs.Entity Interest(HeadlessScene scene, float x, float z, TerrainInterest interest)
    {
        var e = scene.World.CreateEntity();
        e.Set(new Transform { Position = new Vector3D<float>(x, 10, z), Rotation = Quaternion<float>.Identity, Scale = Vector3D<float>.One });
        e.Set(interest);
        return e;
    }

    internal static TerrainInterest View(float radius) => new() { ColliderRadius = 16, DrawRadius = radius };
    private static TerrainInterest Colliders(float radius) => new() { ColliderRadius = radius };

    /// <summary>Runs streaming until it's quiet: nothing loading and nothing changing for a second of frames (at least
    /// <paramref name="frames"/> frames, at most 20 s); with the clock, if any, moved on a frame each frame.</summary>
    internal static void Settle(ChunkLoadSystem load, int frames = 60, ClearSkies.Engine.Core.Time? time = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        int quiet = 0, last = -1;
        for (int i = 0; (i < frames || quiet < 60) && DateTime.UtcNow < deadline; i++)
        {
            Frame(load);
            time?.Advance(1 / 60.0);
            bool idle = load.ColumnsInFlight == 0 && load.LoadedChunks == last;
            quiet = idle ? quiet + 1 : 0;
            last = load.LoadedChunks;
            if (load.ColumnsInFlight > 0) Thread.Sleep(1);
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ChunkLoadSystem, ChunkPipelineSystem> Pipelines = new();

    /// <summary>One frame of streaming: what's wanted, then the pipeline making it, as the game runs them.</summary>
    internal static void Frame(ChunkLoadSystem load)
    {
        load.Update(1 / 60f);
        PipelineOf(load).Update(1 / 60f);
    }

    /// <summary>The pipeline system <see cref="Streaming(HeadlessScene, IChunkStore, int, float)"/> made for it.</summary>
    internal static ChunkPipelineSystem PipelineOf(ChunkLoadSystem load)
        => Pipelines.GetValue(load, _ => throw new InvalidOperationException("Not made by Streaming"));

    internal static ChunkLoadSystem Streaming(HeadlessScene scene, SaveDatabase db, int maxChunks = 100_000, float viewDistance = 64) =>
        Streaming(scene, new SavedChunkStore(db), maxChunks, viewDistance);

    /// <summary>The scene's world streamed, as the game's: edits to it ask streaming what's there.</summary>
    internal static ChunkLoadSystem Streaming(HeadlessScene scene, IChunkStore store, int maxChunks = 100_000, float viewDistance = 64)
    {
        var pipeline = scene.ChunkPipeline;
        scene.Streamed = true;
        var load = new ChunkLoadSystem(scene.World, scene.WorldVolume, new ChunkCountBudget(maxChunks), pipeline.Pipeline, () => new Flat(),
                                       viewDistance, minChunkY: 0, store);
        Pipelines.Add(load, pipeline);
        ((ClearSkies.Engine.Commands.Handlers.EditVoxelsHandler)scene.Commands.HandlerFor(ClearSkies.Engine.Commands.CommandIds.EditVoxels)!).Terrain = load;
        return load;
    }

    [Fact]
    public void WithNoViewNothingIsStreamed()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var load = Streaming(scene, db);
        Settle(load); // a dedicated server with nobody on it
        Assert.Equal(0, load.LoadedChunks);
        Assert.False(load.IsTerrainLoaded(new Vector3D<float>(0, 0, 0), 16));
    }

    [Fact]
    public void WobblingOnAColumnBoundaryDoesNotReloadTheViewEdge()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var load = Streaming(scene, db);
        var player = Interest(scene, 0.1f, 5, View(64));
        Settle(load);
        int rebuilds = load.Rebuilds;
        for (int i = 0; i < 20; i++)
        {
            player.Get<Transform>().Position = new Vector3D<float>(i % 2 == 0 ? -0.1f : 0.1f, 10, 5);
            Settle(load, 5);
        }
        Assert.Equal(rebuilds, load.Rebuilds);

        // Clearly into the next column: the view follows.
        player.Get<Transform>().Position = new Vector3D<float>(-5, 10, 5);
        Settle(load, 5);
        Assert.Equal(rebuilds + 1, load.Rebuilds);
    }

    [Fact]
    public void TheBudgetLimitsWhatLoadsAndTheNearestIsKept()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var unlimited = Streaming(scene, db);
        var player = Interest(scene, 16, 16, View(64));
        Settle(unlimited);
        int all = unlimited.LoadedChunks;

        using var scene2 = new HeadlessScene();
        int max = all - 2 * 8; // two columns short of everything in view (a column is 8 chunks of stone)
        var limited = Streaming(scene2, db, maxChunks: max);
        Interest(scene2, 16, 16, View(64));
        Settle(limited);
        Assert.InRange(limited.LoadedChunks, max - 8, max);
        Assert.True(limited.IsTerrainLoaded(new Vector3D<float>(16, 0, 16), 16)); // the nearest loaded first
        Assert.False(limited.IsTerrainLoaded(new Vector3D<float>(16, 0, 16), 64));
    }

    [Fact]
    public void FullDetailLoadsInABallOfGroupsAroundTheView()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var load = Streaming(scene, db, viewDistance: 256);
        load.SetLevels(128, new[] { 512f }); // full detail to 2 groups of 2x2x2 chunks
        var player = Interest(scene, 16, 16, View(256)); // in group (0, 0, 0); stone fills layers -8 to -1
        Settle(load);
        bool Loaded(int x, int y, int z) => scene.WorldVolume.IsLoaded(new ChunkPosition(x, y, z));
        // Under the view, the groups 2 down: layers -4 to -1, not the stone below them.
        Assert.True(Loaded(0, -1, 0) && Loaded(0, -4, 0));
        Assert.False(Loaded(0, -5, 0));
        // A group across, one down: layers -2 and -1 (group middles, from the middle of the view's chunk).
        Assert.True(Loaded(2, -2, 0));
        Assert.False(Loaded(2, -3, 0));
        // Two groups across: only the view's own height, where there's no stone.
        Assert.False(Loaded(4, -1, 0));

        // Down among the stone, the ball goes with the view.
        player.Get<Transform>().Position = new Vector3D<float>(16, -200, 16); // layer -7, group -4
        Settle(load);
        Assert.True(Loaded(0, -8, 0) && Loaded(2, -7, 0));
        Assert.False(Loaded(0, -1, 0)); // 3 groups up
    }

    [Fact]
    public void ACoarseLevelLoadsPastFullDetailAndIsLeftOutWhereFullDetailIs()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var load = Streaming(scene, db, viewDistance: 256);
        load.SetLevels(64, new[] { 256f }); // full detail to one group; level 1 (64-block chunks) to 4 chunks
        using var drawn = scene.World.SubscribeComponentAdded((in DefaultEcs.Entity e, in Chunk _) => e.Set<Rendered>()); // as the game's view
        var time = new ClearSkies.Engine.Core.Time();
        var pipeline = PipelineOf(load).Pipeline;
        pipeline.Clock = () => time.TotalSeconds;
        var baked = pipeline.Add(new BakedStandIn(pipeline, load.Voxels));
        pipeline.Add(new ShownStage(pipeline, time, baked, load));
        var player = Interest(scene, 16, 16, View(256));
        var deadline = DateTime.UtcNow.AddSeconds(20);
        do { Settle(load, time: time); } while (!load.LevelsIdle && DateTime.UtcNow < deadline);
        Assert.True(load.LevelsIdle);

        var level = load.Levels[1];
        Assert.Equal(2, level.Volume.Scale);
        var near = new ChunkPosition(0, -1, 0); // over full chunks (0..1, -2..-1, 0..1), which full detail has
        var far = new ChunkPosition(3, -1, 0);  // past full detail
        Assert.True(level.Volume.IsLoaded(near) && level.Volume.IsLoaded(far));
        Assert.True(level.Volume.GetData(far)!.IsUniform(out var block) && block == BlockId.Stone);
        Assert.False(level.Volume.IsLoaded(new ChunkPosition(3, 0, 0))); // air
        Assert.False(level.Volume.IsLoaded(new ChunkPosition(5, -1, 0))); // out of range
        var fullDetail = load.Levels[0];
        var under = new ChunkPosition(0, -1, 0);
        // How each is drawn now: 1, not at all (see Shown.Dissolve).
        float Drawn(ChunkLoadSystem.TerrainLevel l, ChunkPosition p)
            => l.Volume.GetEntry(p)?.Entity is { } e && e.Has<Shown>() ? e.Get<Shown>().Dissolve(time.TotalSeconds) : 1f;
        // Nothing's baked yet: nothing's drawn.
        Assert.Equal(1f, Drawn(level, far));
        Assert.Equal(1f, Drawn(fullDetail, under));

        var fade = ShownStage.FadeSeconds;
        try
        {
            // Baked, with fades at once: level 1 is drawn past full detail, and not where full detail has its eight,
            // which is drawn there.
            ShownStage.FadeSeconds = 0f;
            baked.Baked = true;
            Frames(load, time, 10);
            Assert.Equal(1f, Drawn(level, near));
            Assert.Equal(0f, Drawn(level, far));
            Assert.Equal(0f, Drawn(level, new ChunkPosition(0, -4, 0))); // below full detail's ball
            Assert.Equal(0f, Drawn(fullDetail, under));

            // Moving on a little, full detail leaves the near chunks behind: level 1 dissolves back in over them as they
            // dissolve out, and they're held till it's in.
            ShownStage.FadeSeconds = 1f;
            player.Get<Transform>().Position = new Vector3D<float>(16 + 128, 10, 16);
            Frames(load, time, 10);
            Assert.True(fullDetail.Volume.IsLoaded(under));
            float coming = Drawn(level, near), going = Drawn(fullDetail, under);
            Assert.InRange(coming, 0.01f, 0.99f);
            Assert.Equal(-coming, going); // the pixels it leaves out
            Frames(load, time, 90);
            Assert.False(fullDetail.Volume.IsLoaded(under));
            Assert.Equal(0f, Drawn(level, near));
        }
        finally { ShownStage.FadeSeconds = fade; }

        // The level streams with the view like full detail: what's left behind unloads.
        player.Get<Transform>().Position = new Vector3D<float>(16 + 1024, 10, 16);
        do { Settle(load, time: time); } while (!load.LevelsIdle && DateTime.UtcNow < deadline.AddSeconds(20));
        Assert.False(level.Volume.IsLoaded(far));
        Assert.True(level.Volume.IsLoaded(new ChunkPosition(16 + 3, -1, 0)));

        // Off: full detail as far as the view again, and the level is gone.
        load.SetLevels(0, Array.Empty<float>());
        Assert.Null(load.FullDetailRadius);
        Assert.Single(load.Levels); // full detail
        Assert.False(level.Root.IsAlive);
    }

    private static void Frames(ChunkLoadSystem load, ClearSkies.Engine.Core.Time time, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            Frame(load);
            time.Advance(1 / 60.0);
        }
    }

    /// <summary>Stands in for first bake (lit and baked, which need a GPU): a chunk's baked once <see cref="Baked"/>
    /// is set.</summary>
    private sealed class BakedStandIn(ChunkPipeline pipeline, Stage voxels) : FrameStage
    {
        public bool Baked;
        public override bool AppliesTo(PipelineGrid grid) => voxels.AppliesTo(grid);
        public override IReadOnlyList<ClearSkies.Engine.Pipeline.Input> InputsFor(PipelineGrid grid) => new ClearSkies.Engine.Pipeline.Input[] { new(voxels, Relation.Self) };
        public override StageResult Run(PipelineChunk record, StageInputs inputs)
            => Baked ? StageResult.Done(pipeline.EntryOf(record.Key)) : StageResult.Waiting;
    }

    [Fact]
    public void ChangingOnlyTheRadiiKeepsTheLevels()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var load = Streaming(scene, db, viewDistance: 256);
        load.SetLevels(64, new[] { 256f });
        var level = load.Levels[1];
        load.SetLevels(128, new[] { 384f, 100000f });
        Assert.False(level.Root.IsAlive);
        Assert.Equal(128f, load.FullDetailRadius);
        Assert.Equal(GridStore.ScaledReach, load.Levels[2].Radius); // as far as a scaled grid's index reaches

        level = load.Levels[1];
        load.SetLevels(128, new[] { 512f, 2048f });
        Assert.Same(level, load.Levels[1]);
        Assert.Equal(512f, level.Radius);

    }

    [Fact]
    public void AColliderInterestAwayFromTheViewGetsItsTerrainUntilItLeaves()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var load = Streaming(scene, db);
        Interest(scene, 16, 16, View(64));
        Settle(load);
        int viewOnly = load.LoadedChunks;

        var far = new Vector3D<float>(3200, 0, 3200);
        var ship = Interest(scene, far.X, far.Z, Colliders(48));
        Settle(load);
        Assert.True(load.IsTerrainLoaded(far, 32));
        Assert.True(load.LoadedChunks > viewOnly);

        ship.Dispose();
        Settle(load);
        Assert.False(load.IsTerrainLoaded(far, 32));
        Assert.Equal(viewOnly, load.LoadedChunks);
    }

    [Fact]
    public void WhenTheBudgetIsShortTheTerrainNearestAnyInterestIsKept()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var unlimited = Streaming(scene, db, viewDistance: 160);
        Interest(scene, 16, 16, View(160));
        Settle(unlimited);
        int view = unlimited.LoadedChunks;

        // A colliders-only interest far off, with the budget short of the whole view by more than its terrain: its
        // terrain is near it, so it's kept, and the view's far edge goes instead.
        using var scene2 = new HeadlessScene();
        var limited = Streaming(scene2, db, maxChunks: view - 4 * 8, viewDistance: 160);
        Interest(scene2, 16, 16, View(160));
        var far = new Vector3D<float>(3200, 0, 3200);
        Interest(scene2, far.X, far.Z, Colliders(48));
        Settle(limited);
        Assert.True(limited.IsTerrainLoaded(far, 32));
        Assert.True(limited.IsTerrainLoaded(new Vector3D<float>(16, 0, 16), 64));
        Assert.False(limited.IsTerrainLoaded(new Vector3D<float>(16, 0, 16), 160));
    }

    [Fact]
    public void TheFogSitsWhereTheDrawnTerrainEnds()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var load = Streaming(scene, db, viewDistance: 500);
        Assert.Equal(300 - 2 * ChunkData.Size, load.DrawnTo(300)); // the draw radius, less two chunks
        load.SetLevels(64, new[] { 256f, 1024f });
        Assert.Equal(1024 - 2 * 128, load.DrawnTo(300)); // the farthest level, less two of its (128-block) chunks
    }
}
