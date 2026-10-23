using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>When the Mesh stage meshes a chunk meshed without its neighbours (the streamed world's way, see
/// <see cref="ChunkVolume.MeshIgnoresNeighbours"/>): not at all while it's buried in opaque chunks, and again when a
/// neighbour's coming, going or edit changes what it shows (buried or not, or a transparent face on the border).</summary>
public class PipelineMeshTests
{
    private static readonly ChunkPosition Centre = new(0, 0, 0);

    private static readonly ChunkPosition[] Sides =
    {
        new(-1, 0, 0), new(1, 0, 0), new(0, -1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1),
    };

    /// <summary>A volume in the pipeline (wanted as its chunks come and go, as a ship's are), drawn, and meshed without
    /// its neighbours. Its meshes aren't shown (there's no Upload stage): what's seen is which chunks mesh.</summary>
    private sealed class Scene
    {
        private readonly Queue<Action> _jobs = new();
        public readonly ChunkPipelineSystem System;
        public readonly MeshStage Mesh;
        public readonly ChunkVolume Volume;

        public Scene()
        {
            var world = new World();
            System = new ChunkPipelineSystem(world, new ChunkPipeline(_jobs.Enqueue));
            Mesh = System.Pipeline.Add(new MeshStage(System.Pipeline, new ChunkMeshes()));
            var root = world.CreateEntity();
            root.Set(new DynamicGrid());
            root.Set<Rendered>();
            Volume = new ChunkVolume(root, world) { MeshIgnoresNeighbours = true };
        }

        private PipelineChunk Record(ChunkPosition pos) => System.Pipeline.Get(Volume.GetEntry(pos)!.Entity.Get<InPipeline>().Key)!;

        public void Settle()
        {
            for (int i = 0; i < 10; i++)
            {
                System.Update(1 / 60f);
                while (_jobs.TryDequeue(out var job)) job();
            }
        }

        /// <summary>Whether the chunk at <paramref name="pos"/> is to mesh again, once its neighbours' blocks are taken
        /// (then all settles).</summary>
        public bool Remeshes(ChunkPosition pos)
        {
            System.Update(1 / 60f);
            bool meshing = Record(pos).State(Mesh) != SlotState.Done;
            Settle();
            return meshing;
        }

        /// <summary>Whether the chunk at <paramref name="pos"/> gets no mesh: it's buried.</summary>
        public bool Buried(ChunkPosition pos) => Mesh.Prepare(Record(pos)) == null;
    }

    private static ChunkData Filled(BlockId id)
    {
        var data = new ChunkData();
        data.LoadBlockBytes(Enumerable.Repeat((byte)id, ChunkData.Volume).ToArray());
        data.Compact();
        return data;
    }

    /// <summary>A stone chunk at <see cref="Centre"/> and one on each side but <paramref name="skip"/>, settled.</summary>
    private static Scene StoneBlock(ChunkPosition? skip = null)
    {
        var scene = new Scene();
        scene.Volume.AddChunk(Centre, Filled(BlockId.Stone));
        foreach (var side in Sides)
            if (side != skip) scene.Volume.AddChunk(side, Filled(BlockId.Stone));
        scene.Settle();
        return scene;
    }

    [Fact]
    public void A_stone_chunk_with_stone_on_every_side_is_buried()
    {
        var scene = StoneBlock();
        Assert.True(scene.Buried(Centre));
        Assert.False(scene.Buried(Sides[0])); // nothing beyond it
    }

    [Fact]
    public void A_chunk_beside_nothing_is_not_buried()
    {
        Assert.False(StoneBlock(skip: Sides[3]).Buried(Centre));
    }

    [Fact]
    public void A_chunk_beside_air_glass_or_a_model_block_is_not_buried()
    {
        int s = ChunkData.Size;
        foreach (var id in new[] { BlockId.Air, BlockId.Glass, BlockId.Water, BlockId.Lever })
        {
            var scene = StoneBlock();
            scene.Volume.SetBlock(s + 5, 5, 5, id); // inside the +X neighbour
            Assert.False(scene.Buried(Centre));
        }
    }

    [Fact]
    public void A_mix_of_opaque_blocks_buries_as_well_as_one()
    {
        var scene = StoneBlock();
        scene.Volume.SetBlock(ChunkData.Size + 5, 5, 5, BlockId.Dirt);
        Assert.True(scene.Buried(Centre));
    }

    [Fact]
    public void Loading_the_last_side_remeshes_the_chunk_it_buries()
    {
        var scene = StoneBlock(skip: Sides[5]);
        scene.Volume.AddChunk(Sides[5], new ChunkData()); // air: buries nothing
        Assert.False(scene.Remeshes(Centre));
        scene.Volume.RemoveChunk(Sides[5]);
        scene.Settle();

        scene.Volume.AddChunk(Sides[5], Filled(BlockId.Stone));
        Assert.True(scene.Remeshes(Centre));
        Assert.True(scene.Buried(Centre));
    }

    [Fact]
    public void Unloading_a_side_remeshes_the_chunk_it_buried()
    {
        var scene = StoneBlock();
        scene.Volume.RemoveChunk(Sides[2]);
        Assert.True(scene.Remeshes(Centre));
        Assert.False(scene.Buried(Centre));
    }

    [Fact]
    public void Digging_into_a_side_and_filling_it_back_remeshes_the_chunk_beside_it()
    {
        int s = ChunkData.Size;
        var scene = StoneBlock();

        scene.Volume.SetBlock(5, s + 5, 5, BlockId.Air); // inside the +Y neighbour, away from the border
        Assert.True(scene.Remeshes(Centre));
        Assert.False(scene.Buried(Centre));

        scene.Volume.SetBlock(6, s + 5, 5, BlockId.Air); // still dug into: nothing changes for the centre
        Assert.False(scene.Remeshes(Centre));

        scene.Volume.FillBox(new(0, s, 0), new(s - 1, 2 * s - 1, s - 1), BlockId.Stone, BlockOrientation.Upright);
        Assert.True(scene.Remeshes(Centre));
        Assert.True(scene.Buried(Centre));
    }

    /// <summary>Two chunks side by side along X, settled.</summary>
    private static (Scene Scene, ChunkPosition A, ChunkPosition B) TwoChunks()
    {
        var scene = new Scene();
        scene.Volume.SetBlock(0, 0, 0, BlockId.Stone);
        scene.Volume.SetBlock(ChunkData.Size, 0, 0, BlockId.Stone);
        scene.Settle();
        return (scene, new ChunkPosition(0, 0, 0), new ChunkPosition(1, 0, 0));
    }

    [Fact]
    public void An_edit_beside_a_transparent_block_across_a_border_remeshes_its_chunk()
    {
        int s = ChunkData.Size;
        var (scene, a, _) = TwoChunks();
        scene.Volume.SetBlock(s - 1, 4, 4, BlockId.Glass);
        scene.Settle();

        scene.Volume.SetBlock(s + 8, 8, 4, BlockId.Stone); // not on the border
        Assert.False(scene.Remeshes(a));

        scene.Volume.SetBlock(s, 4, 4, BlockId.Stone); // beside the glass: its face is now hidden
        Assert.True(scene.Remeshes(a));

        scene.Volume.FillBox(new(s, 0, 0), new(s + 2, 6, 6), BlockId.Air, BlockOrientation.Upright);
        Assert.True(scene.Remeshes(a));
    }

    [Fact]
    public void Loading_a_chunk_beside_a_transparent_border_remeshes_it()
    {
        int s = ChunkData.Size;
        var (scene, a, _) = TwoChunks();
        scene.Volume.SetBlock(4, 4, s - 1, BlockId.Glass); // on a's +Z border, where nothing is loaded yet
        scene.Settle();

        scene.Volume.AddChunk(new ChunkPosition(0, 0, 1), new ChunkData());
        Assert.True(scene.Remeshes(a));
    }
}
