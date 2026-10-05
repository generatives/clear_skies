using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Terrain streaming with nothing drawn (headless): limited by a count of chunks.</summary>
public class HeadlessStreamingTests
{
    /// <summary>Stone below y = 0, air above.</summary>
    private sealed class Flat : IWorldGenerator
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

    private static DefaultEcs.Entity Interest(HeadlessScene scene, float x, float z, TerrainInterest interest)
    {
        var e = scene.World.CreateEntity();
        e.Set(new Transform { Position = new Vector3D<float>(x, 10, z), Rotation = Quaternion<float>.Identity, Scale = Vector3D<float>.One });
        e.Set(interest);
        return e;
    }

    private static TerrainInterest View(float radius) => new() { ColliderRadius = 16, DrawRadius = radius };
    private static TerrainInterest Colliders(float radius) => new() { ColliderRadius = radius };

    /// <summary>Runs streaming until it's quiet: nothing loading and nothing changing for a second of frames (at least
    /// <paramref name="frames"/> frames, at most 20 s).</summary>
    private static void Settle(ChunkLoadSystem load, int frames = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        int quiet = 0, last = -1;
        for (int i = 0; (i < frames || quiet < 60) && DateTime.UtcNow < deadline; i++)
        {
            load.Update(1 / 60f);
            bool idle = load.ColumnsInFlight == 0 && load.LoadedChunks == last;
            quiet = idle ? quiet + 1 : 0;
            last = load.LoadedChunks;
            if (load.ColumnsInFlight > 0) Thread.Sleep(1);
        }
    }

    private static ChunkLoadSystem Streaming(HeadlessScene scene, SaveDatabase db, int maxChunks = 100_000, float viewDistance = 64) =>
        new(scene.World, scene.WorldVolume, new ChunkCountBudget(maxChunks), () => new Flat(), viewDistance, minChunkY: 0,
            new DatabaseChunkStore(db));

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
}
