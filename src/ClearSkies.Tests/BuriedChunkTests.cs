using ClearSkies.Engine.Voxels;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Chunks buried in opaque chunks (<see cref="ChunkVolume.IsBuried"/>), which get no mesh: which ones are, and
/// the remeshing that keeps that right as neighbours load, unload and are edited.</summary>
public class BuriedChunkTests
{
    private static readonly ChunkPosition Centre = new(0, 0, 0);

    private static readonly ChunkPosition[] Sides =
    {
        new(-1, 0, 0), new(1, 0, 0), new(0, -1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1),
    };

    private static ChunkData Filled(BlockId id)
    {
        var data = new ChunkData();
        data.LoadBlockBytes(Enumerable.Repeat((byte)id, ChunkData.Volume).ToArray());
        data.Compact();
        return data;
    }

    /// <summary>A streamed-world volume (meshes ignore their neighbours) with a stone chunk at <see cref="Centre"/> and
    /// one on each side but <paramref name="skip"/>, the centre's remesh flag cleared.</summary>
    private static (ChunkVolume Volume, ChunkEntry Centre) StoneBlock(ChunkPosition? skip = null)
    {
        var world = new DefaultEcs.World();
        var volume = new ChunkVolume(world.CreateEntity(), world) { MeshIgnoresNeighbours = true };
        volume.AddChunk(Centre, Filled(BlockId.Stone));
        foreach (var side in Sides)
            if (side != skip) volume.AddChunk(side, Filled(BlockId.Stone));
        var centre = volume.GetEntry(Centre)!;
        centre.Entity.Remove<NeedsRemeshFlag>();
        return (volume, centre);
    }

    [Fact]
    public void A_stone_chunk_with_stone_on_every_side_is_buried()
    {
        var (volume, _) = StoneBlock();
        Assert.True(volume.IsBuried(Centre));
        Assert.False(volume.IsBuried(Sides[0])); // nothing loaded beyond it
    }

    [Fact]
    public void A_chunk_beside_nothing_loaded_is_not_buried()
    {
        var (volume, _) = StoneBlock(skip: Sides[3]);
        Assert.False(volume.IsBuried(Centre));
    }

    [Fact]
    public void A_chunk_beside_air_glass_or_a_model_block_is_not_buried()
    {
        int s = ChunkData.Size;
        foreach (var id in new[] { BlockId.Air, BlockId.Glass, BlockId.Water, BlockId.Lever })
        {
            var (volume, _) = StoneBlock();
            volume.SetBlock(s + 5, 5, 5, id); // inside the +X neighbour
            Assert.False(volume.IsBuried(Centre));
        }
    }

    [Fact]
    public void A_mix_of_opaque_blocks_buries_as_well_as_one()
    {
        var (volume, _) = StoneBlock();
        volume.SetBlock(ChunkData.Size + 5, 5, 5, BlockId.Dirt);
        Assert.True(volume.IsBuried(Centre));
    }

    [Fact]
    public void Loading_the_last_side_remeshes_the_chunk_it_buries()
    {
        var (volume, centre) = StoneBlock(skip: Sides[5]);
        volume.AddChunk(Sides[5], new ChunkData()); // air: buries nothing
        Assert.False(centre.Entity.Has<NeedsRemeshFlag>());
        volume.RemoveChunk(Sides[5]);

        volume.AddChunk(Sides[5], Filled(BlockId.Stone));
        Assert.True(volume.IsBuried(Centre));
        Assert.True(centre.Entity.Has<NeedsRemeshFlag>());
    }

    [Fact]
    public void Unloading_a_side_remeshes_the_chunk_it_buried()
    {
        var (volume, centre) = StoneBlock();
        volume.RemoveChunk(Sides[2]);
        Assert.False(volume.IsBuried(Centre));
        Assert.True(centre.Entity.Has<NeedsRemeshFlag>());
    }

    [Fact]
    public void Digging_into_a_side_and_filling_it_back_remeshes_the_chunk_beside_it()
    {
        int s = ChunkData.Size;
        var (volume, centre) = StoneBlock();

        volume.SetBlock(5, s + 5, 5, BlockId.Air); // inside the +Y neighbour, away from the border
        Assert.False(volume.IsBuried(Centre));
        Assert.True(centre.Entity.Has<NeedsRemeshFlag>());

        centre.Entity.Remove<NeedsRemeshFlag>();
        volume.SetBlock(6, s + 5, 5, BlockId.Air); // still dug into: nothing changes for the centre
        Assert.False(centre.Entity.Has<NeedsRemeshFlag>());

        volume.FillBox(new(0, s, 0), new(s - 1, 2 * s - 1, s - 1), BlockId.Stone, BlockOrientation.Upright);
        Assert.True(volume.IsBuried(Centre));
        Assert.True(centre.Entity.Has<NeedsRemeshFlag>());
    }
}
