using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Transparent blocks (glass, water): their registration, how GreedyMesher splits and culls their faces, and
/// the opacity carried in a chunk vertex.</summary>
public class TransparentBlockTests
{
    [Theory]
    [InlineData(BlockId.Glass)]
    [InlineData(BlockId.Water)]
    public void Glass_and_water_are_registered_transparent_and_placeable(BlockId id)
    {
        Assert.True(BlockRegistry.IsDefined(id));
        var def = BlockRegistry.Get(id);
        Assert.True(def.Transparent);
        Assert.True(def.IsFullCube);
        Assert.True(def.Opacity < 15); // lets light through
        Assert.NotNull(def.Texture);
        Assert.Contains(id, BlockActionSystem.PlaceableBlocks);
    }

    [Fact]
    public void Water_is_passable_and_glass_collides()
    {
        Assert.False(BlockRegistry.Get(BlockId.Water).Collides);
        Assert.True(BlockRegistry.Get(BlockId.Glass).Collides);
    }

    [Fact]
    public void Opaque_block_keeps_its_face_against_glass_and_hides_the_glass_face()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Stone);
        data.Set(5, 4, 4, BlockId.Glass);
        var mesher = new GreedyMesher();
        var (verts, _) = mesher.Mesh(data, null, null, null, null, null, null);

        // Stone shows all 6 faces (its +X face is behind glass); glass shows 5 (not the one against the stone).
        Assert.Equal(6 * 4, verts.Count);
        Assert.Equal(5 * 4, mesher.TransparentVertices.Count);
        Assert.Equal(5 * 6, mesher.TransparentIndices.Count);
        Assert.Equal(mesher.TransparentVertices.Count, mesher.TransparentAlphas.Count);
        Assert.DoesNotContain(mesher.TransparentVertices, v => v.Normal.X < -0.5f);
    }

    [Fact]
    public void Faces_between_blocks_of_one_transparent_type_are_hidden()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Water);
        data.Set(5, 4, 4, BlockId.Water);
        var mesher = new GreedyMesher();
        var (verts, _) = mesher.Mesh(data, null, null, null, null, null, null);

        Assert.Empty(verts);
        // A 2x1x1 box: the four long sides merge to one quad each, plus the two ends.
        Assert.Equal(6 * 4, mesher.TransparentVertices.Count);
    }

    [Fact]
    public void Faces_between_different_transparent_blocks_are_both_drawn()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Water);
        data.Set(5, 4, 4, BlockId.Glass);
        var mesher = new GreedyMesher();
        mesher.Mesh(data, null, null, null, null, null, null);

        Assert.Equal(12 * 4, mesher.TransparentVertices.Count);
    }

    [Fact]
    public void Transparent_alpha_is_the_blocks_opacity()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Water);
        data.Set(8, 4, 4, BlockId.Glass);
        var mesher = new GreedyMesher();
        mesher.Mesh(data, null, null, null, null, null, null);

        byte water = (byte)MathF.Round(BlockRegistry.Get(BlockId.Water).EffectiveAlpha * 255f);
        Assert.True(water < 255);
        Assert.Contains(water, mesher.TransparentAlphas);
        Assert.Contains((byte)255, mesher.TransparentAlphas); // glass: its texture's alpha alone
    }

    [Fact]
    public void Ignoring_neighbours_still_hides_a_transparent_face_against_its_own_type()
    {
        int last = ChunkData.Size - 1;
        var data = new ChunkData();
        data.Set(last, 4, 4, BlockId.Water);
        data.Set(last, 8, 4, BlockId.Stone);
        var px = new ChunkData();
        px.Set(0, 4, 4, BlockId.Water);
        px.Set(0, 8, 4, BlockId.Stone);
        var mesher = new GreedyMesher();

        var (verts, _) = mesher.Mesh(data, null, px, null, null, null, null, neighboursForTransparentOnly: true);
        Assert.Equal(5 * 4, mesher.TransparentVertices.Count); // the water's +X face is hidden by the water beyond
        Assert.Equal(6 * 4, verts.Count);                      // the stone's isn't: opaque border faces are all drawn

        (verts, _) = mesher.Mesh(data, null, px, null, null, null, null);
        Assert.Equal(5 * 4, mesher.TransparentVertices.Count);
        Assert.Equal(5 * 4, verts.Count);
    }

    [Fact]
    public void Chunk_vertex_carries_alpha()
    {
        var v = new Vertex(new Vector3D<float>(1, 2, 3), new Vector3D<float>(0, 1, 0), new Vector3D<float>(0.2f, 0.4f, 0.6f));
        var packed = ChunkVertex.Pack(v, 153);
        Assert.Equal(153 / 255f, packed.Alpha, 4);
        Assert.Equal(1f, ChunkVertex.Pack(v).Alpha);
        var back = packed.Unpack();
        Assert.Equal(v.Position, back.Position);
        Assert.Equal(0.4f, back.Color.Y, 2);
    }
}
