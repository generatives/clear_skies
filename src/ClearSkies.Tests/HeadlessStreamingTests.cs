using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Terrain streaming with no GPU store (headless).</summary>
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

        public ulong ColumnLayers(int chunkX, int chunkZ, int minChunkY) => ulong.MaxValue;
    }

    private static DefaultEcs.Entity Interest(HeadlessScene scene, float x, float z, float radius, TerrainInterestKind kind)
    {
        var e = scene.World.CreateEntity();
        e.Set(new Transform { Position = new Vector3D<float>(x, 10, z), Rotation = Quaternion<float>.Identity, Scale = Vector3D<float>.One });
        e.Set(new TerrainInterest { Radius = radius, Kind = kind });
        return e;
    }

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

    private static ChunkLoadSystem Streaming(HeadlessScene scene, SaveDatabase db) =>
        new(scene.World, scene.WorldVolume, store: null, () => new Flat(), viewDistance: 64, minChunkY: 0, new DatabaseChunkStore(db));

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
        var player = Interest(scene, 0.1f, 5, 64, TerrainInterestKind.Full);
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
}
