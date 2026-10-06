using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Transparent blocks (glass, water): their registration, how GreedyMesher puts their faces in their layer's
/// mesh and culls them, the neighbour remeshing that culling across chunk borders needs, and the opacity carried in a
/// chunk vertex.</summary>
public class TransparentBlockTests
{
    [Fact]
    public void Glass_is_a_cut_out_transparent_cube_that_collides_and_is_placeable()
    {
        Assert.True(BlockRegistry.IsDefined(BlockId.Glass));
        var def = BlockRegistry.Get(BlockId.Glass);
        Assert.Equal(RenderLayer.Cutout, def.Layer);
        Assert.True(def.Transparent);
        Assert.True(def.IsFullCube);
        Assert.True(def.Collides);
        Assert.NotNull(def.Texture);
        Assert.Contains(BlockId.Glass, BlockActionSystem.PlaceableBlocks);
        Assert.Equal(RenderLayer.Opaque, BlockRegistry.Get(BlockId.Stone).Layer);
    }

    [Fact]
    public void Water_is_a_translucent_passable_transparent_cube_and_placeable()
    {
        Assert.True(BlockRegistry.IsDefined(BlockId.Water));
        var def = BlockRegistry.Get(BlockId.Water);
        Assert.Equal(RenderLayer.Translucent, def.Layer);
        Assert.True(def.Transparent);
        Assert.True(def.IsFullCube);
        Assert.False(def.Collides);
        Assert.NotNull(def.Texture);
        Assert.Contains(BlockId.Water, BlockActionSystem.PlaceableBlocks);
    }

    [Fact]
    public void Opaque_block_keeps_its_face_against_glass_and_hides_the_glass_face()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Stone);
        data.Set(5, 4, 4, BlockId.Glass);
        var mesher = new GreedyMesher();
        var mesh = mesher.Mesh(data, null, null, null, null, null, null);

        // Stone shows all 6 faces (its +X face is behind glass); glass shows 5 (not the one against the stone), in the
        // cut-out mesh.
        Assert.Equal(6 * 4, mesh.Opaque.Vertices.Count);
        Assert.Equal(5 * 4, mesh.Cutout.Vertices.Count);
        Assert.Equal(5 * 6, mesh.Cutout.Indices.Count);
        Assert.DoesNotContain(mesh.Cutout.Vertices, v => v.Normal.X < -0.5f);
    }

    [Fact]
    public void Faces_between_blocks_of_one_transparent_type_are_hidden()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Glass);
        data.Set(5, 4, 4, BlockId.Glass);
        var mesher = new GreedyMesher();
        var mesh = mesher.Mesh(data, null, null, null, null, null, null);

        Assert.Empty(mesh.Opaque.Vertices);
        // A 2x1x1 box: the four long sides merge to one quad each, plus the two ends.
        Assert.Equal(6 * 4, mesh.Cutout.Vertices.Count);
    }

    [Fact]
    public void Faces_between_water_blocks_are_hidden()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Water);
        data.Set(5, 4, 4, BlockId.Water);
        var mesher = new GreedyMesher();
        var mesh = mesher.Mesh(data, null, null, null, null, null, null);

        Assert.Empty(mesh.Opaque.Vertices);
        Assert.Empty(mesh.Cutout.Vertices);
        Assert.Equal(6 * 4, mesh.Translucent.Vertices.Count);
    }

    [Fact]
    public void Faces_between_different_transparent_blocks_are_both_drawn()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Water);
        data.Set(5, 4, 4, BlockId.Glass);
        var mesher = new GreedyMesher();
        var mesh = mesher.Mesh(data, null, null, null, null, null, null);

        Assert.Equal(6 * 4, mesh.Translucent.Vertices.Count); // water
        Assert.Equal(6 * 4, mesh.Cutout.Vertices.Count);      // glass
    }

    [Fact]
    public void Translucent_quads_carry_their_block()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Water);
        var mesh = new GreedyMesher().Mesh(data, null, null, null, null, null, null);
        Assert.Equal(mesh.Translucent.Vertices.Count / 4, mesh.Translucent.Blocks.Count);
        Assert.All(mesh.Translucent.Blocks, b => Assert.Equal(BlockId.Water, b));

        // Its opacity comes from the block table, by block.
        var quad = ChunkQuad.Pack(mesh.Translucent.Vertices.GetRange(0, 4).ToArray(), BlockId.Water);
        Assert.Equal(BlockId.Water, quad.Block);
        Assert.Equal(BlockRegistry.Get(BlockId.Water).EffectiveAlpha, ChunkQuad.BuildBlockTable()[(int)quad.Block].W);
    }

    [Fact]
    public void Water_across_a_border_is_culled_against_water_when_ignoring_neighbours()
    {
        int last = ChunkData.Size - 1;
        var data = new ChunkData();
        data.Set(last, 4, 4, BlockId.Water);
        var px = new ChunkData();
        px.Set(0, 4, 4, BlockId.Water);
        var mesher = new GreedyMesher();

        var mesh = mesher.Mesh(data, null, px, null, null, null, null, neighboursForTransparentOnly: true);
        Assert.Equal(5 * 4, mesh.Translucent.Vertices.Count);
    }

    [Fact]
    public void An_opaque_model_hides_a_transparent_face_against_it()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Fan);
        data.Set(5, 4, 4, BlockId.Glass);
        var mesher = new GreedyMesher();
        var mesh = mesher.Mesh(data, null, null, null, null, null, null);

        Assert.Equal(5 * 4, mesh.Cutout.Vertices.Count);
    }

    [Fact]
    public void A_model_that_fills_part_of_its_cell_hides_no_transparent_face()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Lever);
        data.Set(5, 4, 4, BlockId.Glass);
        var mesher = new GreedyMesher();
        var mesh = mesher.Mesh(data, null, null, null, null, null, null);

        Assert.Equal(6 * 4, mesh.Cutout.Vertices.Count);
    }

    [Fact]
    public void Ignoring_neighbours_still_hides_a_transparent_face_against_its_own_type()
    {
        int last = ChunkData.Size - 1;
        var data = new ChunkData();
        data.Set(last, 4, 4, BlockId.Glass);
        data.Set(last, 8, 4, BlockId.Stone);
        var px = new ChunkData();
        px.Set(0, 4, 4, BlockId.Glass);
        px.Set(0, 8, 4, BlockId.Stone);
        var mesher = new GreedyMesher();

        var mesh = mesher.Mesh(data, null, px, null, null, null, null, neighboursForTransparentOnly: true);
        Assert.Equal(5 * 4, mesh.Cutout.Vertices.Count); // the glass's +X face is hidden by the glass beyond
        Assert.Equal(6 * 4, mesh.Opaque.Vertices.Count);                  // the stone's isn't: opaque border faces are all drawn

        mesh = mesher.Mesh(data, null, px, null, null, null, null);
        Assert.Equal(5 * 4, mesh.Cutout.Vertices.Count);
        Assert.Equal(5 * 4, mesh.Opaque.Vertices.Count);
    }

    [Fact]
    public void Ignoring_neighbours_still_culls_transparent_faces_against_opaque_blocks()
    {
        int last = ChunkData.Size - 1;
        var data = new ChunkData();
        data.Set(last, 4, 4, BlockId.Glass);
        var px = new ChunkData();
        px.Set(0, 4, 4, BlockId.Stone);
        var mesher = new GreedyMesher();

        var mesh = mesher.Mesh(data, null, px, null, null, null, null, neighboursForTransparentOnly: true);
        Assert.Equal(5 * 4, mesh.Cutout.Vertices.Count); // no glass face drawn on top of the stone's
    }

    /// <summary>A streamed-world volume (meshes ignore their neighbours) with two chunks side by side along X, their
    /// remesh flags cleared.</summary>
    private static (ChunkVolume Volume, ChunkEntry A, ChunkEntry B) TwoStreamedChunks()
    {
        var world = new DefaultEcs.World();
        var volume = new ChunkVolume(world.CreateEntity(), world) { MeshIgnoresNeighbours = true };
        volume.SetBlock(0, 0, 0, BlockId.Stone);
        volume.SetBlock(ChunkData.Size, 0, 0, BlockId.Stone);
        var a = volume.GetEntry(new ChunkPosition(0, 0, 0))!;
        var b = volume.GetEntry(new ChunkPosition(1, 0, 0))!;
        a.Entity.Remove<NeedsRemeshFlag>();
        b.Entity.Remove<NeedsRemeshFlag>();
        return (volume, a, b);
    }

    [Fact]
    public void An_edit_beside_a_transparent_block_across_a_border_remeshes_its_chunk()
    {
        int s = ChunkData.Size;
        var (volume, a, b) = TwoStreamedChunks();
        volume.SetBlock(s - 1, 4, 4, BlockId.Glass);
        a.Entity.Remove<NeedsRemeshFlag>();
        b.Entity.Remove<NeedsRemeshFlag>();

        volume.SetBlock(s, 8, 4, BlockId.Stone); // on the border, but beside air
        Assert.False(a.Entity.Has<NeedsRemeshFlag>());

        volume.SetBlock(s, 4, 4, BlockId.Stone); // beside the glass: its face is now hidden
        Assert.True(a.Entity.Has<NeedsRemeshFlag>());

        a.Entity.Remove<NeedsRemeshFlag>();
        volume.FillBox(new(s, 0, 0), new(s + 2, 6, 6), BlockId.Air, BlockOrientation.Upright);
        Assert.True(a.Entity.Has<NeedsRemeshFlag>());
    }

    [Fact]
    public void Loading_a_chunk_beside_a_transparent_border_remeshes_it()
    {
        int s = ChunkData.Size;
        var (volume, a, _) = TwoStreamedChunks();
        volume.SetBlock(4, 4, s - 1, BlockId.Glass); // on a's +Z border, where nothing is loaded yet
        a.Entity.Remove<NeedsRemeshFlag>();

        volume.AddChunk(new ChunkPosition(0, 0, 1), new ChunkData());
        Assert.True(a.Entity.Has<NeedsRemeshFlag>());
    }
}
