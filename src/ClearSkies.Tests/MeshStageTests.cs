using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Voxels;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Streamed chunks meshed as they load (ChunkMeshSystem.Stage), which then need no mesh job.</summary>
public class MeshStageTests
{
    private static readonly IChunkPreparer Stage = ChunkMeshSystem.StageFor(null);

    private static ChunkData GlassAt(int y)
    {
        var data = new ChunkData();
        data.Set(4, y, 4, BlockId.Glass);
        data.Set(8, 8, 8, BlockId.Stone);
        return data;
    }

    private static ChunkPreparation?[] Prepare(ChunkPosition pos, ChunkData data, Dictionary<ChunkPosition, ChunkData> job)
        => new[] { Stage.Prepare(pos, data, job) };

    [Fact]
    public void Chunks_meshed_in_the_same_job_do_not_remesh_each_other()
    {
        var world = new DefaultEcs.World();
        var volume = new ChunkVolume(world.CreateEntity(), world) { MeshIgnoresNeighbours = true };
        ChunkPosition lower = new(0, 0, 0), upper = new(0, 1, 0);
        // Glass on both sides of the border between them, which is culled against the other.
        var a = GlassAt(ChunkData.Size - 1);
        var b = GlassAt(0);
        var job = new Dictionary<ChunkPosition, ChunkData> { [lower] = a, [upper] = b };
        var prepA = Prepare(lower, a, job);
        var prepB = Prepare(upper, b, job);

        var ea = volume.AddChunk(lower, a, prepA).Entity;
        var eb = volume.AddChunk(upper, b, prepB).Entity;

        foreach (var (e, pos) in new[] { (ea, lower), (eb, upper) })
        {
            Assert.False(e.Has<NeedsRemeshFlag>());
            Assert.True(e.Has<ChunkMeshSystem.PreparedMesh>());
            Assert.True(e.Get<ChunkMeshSystem.PreparedMesh>().CurrentIn(volume, pos));
            Assert.True(e.Get<ChunkMeshSystem.PreparedMesh>().Opaque.QuadCount > 0);
        }
    }

    [Fact]
    public void A_see_through_face_beside_a_chunk_loaded_before_is_out_of_date()
    {
        var world = new DefaultEcs.World();
        var volume = new ChunkVolume(world.CreateEntity(), world) { MeshIgnoresNeighbours = true };
        ChunkPosition lower = new(0, 0, 0), upper = new(0, 1, 0);
        volume.AddChunk(upper, GlassAt(0));

        var a = GlassAt(ChunkData.Size - 1);
        var e = volume.AddChunk(lower, a, Prepare(lower, a, new() { [lower] = a })).Entity;
        Assert.False(e.Get<ChunkMeshSystem.PreparedMesh>().CurrentIn(volume, lower));

        // Without glass on that border, the neighbour makes no difference to it.
        var c = new ChunkData();
        c.Set(8, 8, 8, BlockId.Stone);
        var other = new ChunkPosition(0, 0, 5);
        volume.AddChunk(other.Offset(0, 1, 0), GlassAt(0));
        var ec = volume.AddChunk(other, c, Prepare(other, c, new() { [other] = c })).Entity;
        Assert.True(ec.Get<ChunkMeshSystem.PreparedMesh>().CurrentIn(volume, other));
    }

    [Fact]
    public void Streamed_chunks_come_meshed()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        scene.WorldVolume.MeshIgnoresNeighbours = true; // as the game's
        var load = new ChunkLoadSystem(scene.World, scene.WorldVolume, new ChunkCountBudget(100_000),
                                       () => new HeadlessStreamingTests.Flat(), 64, minChunkY: 0, new SavedChunkStore(db),
                                       new[] { new LightPacking(), Stage });
        HeadlessStreamingTests.Interest(scene, 0, 0, HeadlessStreamingTests.View(64));
        HeadlessStreamingTests.Settle(load);

        Assert.True(load.LoadedChunks > 0);
        foreach (var (_, entry) in scene.WorldVolume.All)
        {
            Assert.True(entry.Entity.Has<ChunkMeshSystem.PreparedMesh>());
            Assert.False(entry.Entity.Has<NeedsRemeshFlag>());
        }
    }
}
