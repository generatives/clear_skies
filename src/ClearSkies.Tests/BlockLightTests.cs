using ClearSkies.Engine.Voxels;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Which blocks stop light (<see cref="BlockDef.BlocksLight"/>), and how an opaque model block (the Fan) hides
/// the faces of blocks against it like an opaque cube.</summary>
public class BlockLightTests
{
    [Theory]
    [InlineData(BlockId.Stone, true)]   // opaque cube
    [InlineData(BlockId.Lamp, true)]
    [InlineData(BlockId.Fan, true)]     // opaque model
    [InlineData(BlockId.Lever, false)]  // model
    [InlineData(BlockId.SteeringWheel, false)]
    [InlineData(BlockId.Air, false)]
    public void Blocks_light_by_category(BlockId id, bool blocks)
        => Assert.Equal(blocks, BlockRegistry.Get(id).BlocksLight);

    [Fact]
    public void An_opaque_model_hides_faces_against_it()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Fan);
        data.Set(3, 4, 4, BlockId.Stone);
        var (verts, _) = new GreedyMesher().Mesh(data, null, null, null, null, null, null);

        // The stone doesn't draw its face on the Fan's side (it would flicker against the model).
        Assert.Equal(5 * 4, verts.Count);
    }

    [Fact]
    public void A_model_that_fills_part_of_its_cell_hides_nothing()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Lever);
        data.Set(3, 4, 4, BlockId.Stone);
        var (verts, _) = new GreedyMesher().Mesh(data, null, null, null, null, null, null);

        Assert.Equal(6 * 4, verts.Count);
    }
}
