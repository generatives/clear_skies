using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Voxels;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Transparent blocks: their registration, how GreedyMesher puts their faces in their layer's mesh and culls
/// them, and the neighbour remeshing that culling across chunk borders needs.</summary>
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
    public void Opaque_block_keeps_its_face_against_glass_and_hides_the_glass_face()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Stone);
        data.Set(5, 4, 4, BlockId.Glass);
        var mesher = new GreedyMesher();
        var (verts, _) = mesher.Mesh(data, null, null, null, null, null, null);

        // Stone shows all 6 faces (its +X face is behind glass); glass shows 5 (not the one against the stone), in the
        // cut-out mesh.
        Assert.Equal(6 * 4, verts.Count);
        Assert.Equal(5 * 4, mesher.CutoutVertices.Count);
        Assert.Equal(5 * 6, mesher.CutoutIndices.Count);
        Assert.DoesNotContain(mesher.CutoutVertices, v => v.Normal.X < -0.5f);
    }

    [Fact]
    public void Faces_between_blocks_of_one_transparent_type_are_hidden()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Glass);
        data.Set(5, 4, 4, BlockId.Glass);
        var mesher = new GreedyMesher();
        var (verts, _) = mesher.Mesh(data, null, null, null, null, null, null);

        Assert.Empty(verts);
        // A 2x1x1 box: the four long sides merge to one quad each, plus the two ends.
        Assert.Equal(6 * 4, mesher.CutoutVertices.Count);
    }

    [Fact]
    public void An_opaque_model_hides_a_transparent_face_against_it()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Fan);
        data.Set(5, 4, 4, BlockId.Glass);
        var mesher = new GreedyMesher();
        mesher.Mesh(data, null, null, null, null, null, null);

        Assert.Equal(5 * 4, mesher.CutoutVertices.Count);
    }

    [Fact]
    public void A_model_that_fills_part_of_its_cell_hides_no_transparent_face()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.Lever);
        data.Set(5, 4, 4, BlockId.Glass);
        var mesher = new GreedyMesher();
        mesher.Mesh(data, null, null, null, null, null, null);

        Assert.Equal(6 * 4, mesher.CutoutVertices.Count);
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

        var (verts, _) = mesher.Mesh(data, null, px, null, null, null, null, neighboursForTransparentOnly: true);
        Assert.Equal(5 * 4, mesher.CutoutVertices.Count); // the glass's +X face is hidden by the glass beyond
        Assert.Equal(6 * 4, verts.Count);                  // the stone's isn't: opaque border faces are all drawn

        (verts, _) = mesher.Mesh(data, null, px, null, null, null, null);
        Assert.Equal(5 * 4, mesher.CutoutVertices.Count);
        Assert.Equal(5 * 4, verts.Count);
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

        mesher.Mesh(data, null, px, null, null, null, null, neighboursForTransparentOnly: true);
        Assert.Equal(5 * 4, mesher.CutoutVertices.Count); // no glass face drawn on top of the stone's
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
