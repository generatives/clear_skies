using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Persistence;
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
    /// <paramref name="frames"/> frames, at most 20 s); with the swaps, if any, updated after it each frame.</summary>
    internal static void Settle(ChunkLoadSystem load, int frames = 60, TerrainSwapSystem? swaps = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        int quiet = 0, last = -1;
        for (int i = 0; (i < frames || quiet < 60) && DateTime.UtcNow < deadline; i++)
        {
            Frame(load);
            swaps?.Update(1 / 60f);
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
        Pipelines.GetValue(load, _ => throw new InvalidOperationException("Not made by Streaming")).Update(1 / 60f);
    }

    internal static ChunkLoadSystem Streaming(HeadlessScene scene, SaveDatabase db, int maxChunks = 100_000, float viewDistance = 64) =>
        Streaming(scene, new SavedChunkStore(db), maxChunks, viewDistance);

    /// <summary>The scene's world streamed, as the game's: edits to it ask streaming what's there.</summary>
    internal static ChunkLoadSystem Streaming(HeadlessScene scene, IChunkStore store, int maxChunks = 100_000, float viewDistance = 64)
    {
        var pipeline = new ChunkPipelineSystem();
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
        var swaps = new TerrainSwapSystem(scene.World, load);
        var player = Interest(scene, 16, 16, View(256));
        var deadline = DateTime.UtcNow.AddSeconds(20);
        do { Settle(load, swaps: swaps); } while (!load.LevelsIdle && DateTime.UtcNow < deadline);
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
        // Nothing's meshed (headless): no level is shown yet, and the full detail is left whole.
        Assert.Equal(ChunkDrawState.Hidden, swaps.DrawState(level, far));
        Assert.Equal(ChunkDrawState.Whole, swaps.DrawState(fullDetail, new ChunkPosition(0, -1, 0)));
        // The fog says what it's held by: a chunk nobody has meshed waits for that, not for its light.
        swaps.FarReadyRadius(player.Get<Transform>().Position);
        Assert.Contains("waiting for its mesh", swaps.FogHeldBy);

        // Meshed (as the renderer would), with swaps at once: level 1 is drawn past full detail, and swaps out where full
        // detail has its eight, which is drawn there; past it, full detail is left out under level 1.
        swaps.FadeSeconds = 0f;
        foreach (var (_, entry) in scene.WorldVolume.All) entry.Entity.Set<ChunkMeshedFlag>();
        foreach (var (_, entry) in level.Volume.All) entry.Entity.Set<ChunkMeshedFlag>();
        for (int i = 0; i < 10; i++) { Frame(load); swaps.Update(1 / 60f); }
        Assert.Equal(1f, swaps.DrawState(level, near).Dissolve);
        Assert.Equal(0f, swaps.DrawState(level, far).Dissolve);
        Assert.Equal(0f, swaps.DrawState(level, new ChunkPosition(0, -4, 0)).Dissolve); // below full detail's ball
        Assert.Equal(ChunkDrawState.Whole, swaps.DrawState(fullDetail, new ChunkPosition(0, -1, 0)));
        Assert.Equal(ChunkDrawState.Hidden, swaps.DrawState(fullDetail, new ChunkPosition(0, -7, 0)));

        // The level streams with the view like full detail: what's left behind unloads.
        player.Get<Transform>().Position = new Vector3D<float>(16 + 1024, 10, 16);
        do { Settle(load, swaps: swaps); } while (!load.LevelsIdle && DateTime.UtcNow < deadline.AddSeconds(20));
        Assert.False(level.Volume.IsLoaded(far));
        Assert.True(level.Volume.IsLoaded(new ChunkPosition(16 + 3, -1, 0)));

        // Off: full detail as far as the view again, and the level is gone.
        load.SetLevels(0, Array.Empty<float>());
        Assert.Null(load.FullDetailRadius);
        Assert.Single(load.Levels); // full detail
        Assert.False(level.Root.IsAlive);
    }

    [Fact]
    public void FlyingOverCoarseTerrainNewLevel1ColumnsShownByLevel2DoNotHoldTheFog()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var load = Streaming(scene, db, viewDistance: 1024);
        load.SetLevels(128, new[] { 512f, 1024f });
        var swaps = new TerrainSwapSystem(scene.World, load) { FadeSeconds = 0f };
        var player = Interest(scene, 16, 16, View(1024));
        var at = new Vector3D<float>(16, 300, 16); // high over the ground: the level-2 chunk at the view's height is air
        player.Get<Transform>().Position = at;
        void LoadAndShow()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            do
            {
                Settle(load, swaps: swaps);
                foreach (var level in load.Levels) foreach (var (_, entry) in level.Volume.All) entry.Entity.Set<ChunkMeshedFlag>();
                for (int i = 0; i < 5; i++) { Frame(load); swaps.Update(1 / 60f); }
            } while (!load.LevelsIdle && DateTime.UtcNow < deadline);
        }
        LoadAndShow();

        // Each move brings level-1 chunks in near the view, under level 2, which shows there meanwhile.
        for (int step = 0; step < 6; step++)
        {
            at.X += 48;
            player.Get<Transform>().Position = at;
            Frame(load);
            swaps.Update(1 / 60f);
            swaps.FarReadyRadius(at);
            Assert.DoesNotContain("level 1 column", swaps.FogHeldBy);
            LoadAndShow();
        }
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

        // Each level, and full detail while there are levels, says how its chunks are drawn (the swaps tell it).
        var swaps = new TerrainSwapSystem(scene.World, load);
        swaps.Update(1 / 60f);
        Assert.True(level.Root.Has<ChunkDrawStates>());
        Assert.True(scene.WorldVolume.Root.Has<ChunkDrawStates>());
        load.SetLevels(128, Array.Empty<float>());
        swaps.Update(1 / 60f);
        Assert.False(scene.WorldVolume.Root.Has<ChunkDrawStates>());
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
    public void ColumnsOnTheirWayAreEntitiesUntilTheyLoad()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var load = Streaming(scene, db);
        var loading = scene.World.GetEntities().With<TerrainColumnLoading>().AsSet();
        var player = Interest(scene, 16, 16, View(64));
        Frame(load); // the scan queues what's wanted, before anything has loaded
        Assert.NotEqual(0, loading.Count);
        Settle(load);
        Assert.Equal(0, loading.Count);
        Assert.Equal(float.PositiveInfinity, player.Get<TerrainScanned>().Radius);
    }

    [Fact]
    public void TheFogSitsAtTheNearestColumnStillLoading()
    {
        using var scene = new HeadlessScene();
        var fog = new FogSystem(scene.World, viewDistance: 500);
        var player = Interest(scene, 16, 16, View(500));
        player.Set(new TerrainScanned { Radius = float.PositiveInfinity });
        var column = scene.World.CreateEntity();
        column.Set(new TerrainColumnLoading { X = 4, Z = 0 }); // 112 blocks out (from x = 16 to the column's edge at 128)
        for (int i = 0; i < 300; i++) fog.Update(1 / 60f);
        Assert.InRange(fog.Distance, 110, 113);

        column.Dispose(); // loaded: the fog opens out to the draw radius
        for (int i = 0; i < 1200; i++) fog.Update(1 / 60f);
        Assert.InRange(fog.Distance, 490, 500);
    }
}
