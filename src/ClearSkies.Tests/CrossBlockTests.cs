using System.Runtime.InteropServices;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Cross blocks (<see cref="BlockShape.Cross"/>: grass, mushrooms, pebbles): their registration, how
/// GreedyMesher draws them as two double-sided diagonal quads in the cut-out mesh, how those pack into
/// <see cref="ChunkQuad"/>s, and the world generator growing them on the ground.</summary>
public class CrossBlockTests
{
    private static readonly BlockId[] Plants =
    {
        BlockId.ShortGrass, BlockId.GrassTuft, BlockId.GrassBlades, BlockId.TallGrass, BlockId.DryGrass,
        BlockId.BrownGrass, BlockId.RedMushroom, BlockId.BrownMushroom, BlockId.TanMushroom, BlockId.Pebbles,
        BlockId.MossyPebbles,
    };

    [Fact]
    public void Plants_are_passable_textured_cross_blocks_that_stop_no_light()
    {
        var atlas = new TextureAtlas(Resource("spritesheet_tiles.png"), Resource("spritesheet_tiles.xml"));
        foreach (var id in Plants)
        {
            var def = BlockRegistry.Get(id);
            Assert.True(BlockRegistry.IsDefined(id));
            Assert.True(def.IsCross);
            Assert.True(def.IsSolid);      // targetable, so it can be broken
            Assert.False(def.Collides);    // walked through
            Assert.False(def.IsFullCube);
            Assert.False(def.BlocksLight);
            Assert.False(def.HidesFaces);
            Assert.True(def.Replaceable);
            Assert.True(atlas.TryGetLayer(def.Texture, out _), $"{id}'s sprite '{def.Texture}' is in the sheet");
        }
        Assert.False(BlockRegistry.Get(BlockId.Stone).IsCross);
        Assert.False(BlockRegistry.Get(BlockId.Glass).Replaceable);
    }

    [Fact]
    public void A_plant_is_four_cut_out_quads_and_hides_nothing()
    {
        var data = new ChunkData();
        data.Set(4, 4, 4, BlockId.TallGrass);
        data.Set(4, 3, 4, BlockId.Stone);
        data.Set(5, 4, 4, BlockId.Stone);
        var mesh = new GreedyMesher().Mesh(data, null, null, null, null, null, null);

        // Both stones keep all six faces: the plant hides neither, and neither hides it.
        Assert.Equal(2 * 6 * 4, mesh.Opaque.Vertices.Count);
        Assert.Equal(4 * 4, mesh.Cutout.Vertices.Count);
        Assert.All(mesh.Cutout.Blocks, b => Assert.Equal(BlockId.TallGrass, b));
        Assert.Empty(mesh.Translucent.Vertices);
    }

    [Fact]
    public void Each_quad_faces_its_normal_and_spans_its_cell()
    {
        var data = new ChunkData();
        data.Set(7, 2, 9, BlockId.RedMushroom);
        var cutout = new GreedyMesher().Mesh(data, null, null, null, null, null, null).Cutout;
        var v = cutout.Vertices;
        for (int q = 0; q < 4; q++)
        {
            // Counter-clockwise seen from the normal's side: the front face, which back-face culling keeps.
            var c0 = v[4 * q].Position;
            var winding = Vector3D.Cross(v[4 * q + 1].Position - c0, v[4 * q + 2].Position - c0);
            Assert.True(Vector3D.Dot(winding, v[4 * q].Normal) > 0.5f);
            for (int c = 0; c < 4; c++)
            {
                var p = v[4 * q + c].Position;
                Assert.InRange(p.X, 7f, 8f); Assert.InRange(p.Y, 2f, 3f); Assert.InRange(p.Z, 9f, 10f);
            }
        }
        // Two diagonals, each seen from both sides.
        Assert.Equal(4, v.Where((_, i) => i % 4 == 0).Select(x => x.Normal).Distinct().Count());
    }

    [Fact]
    public void Plant_quads_decode_to_the_meshers_corners()
    {
        var data = new ChunkData();
        data.Set(0, 0, 0, BlockId.ShortGrass);
        data.Set(31, 31, 31, BlockId.Pebbles);
        data.Set(12, 5, 20, BlockId.TanMushroom);
        var mesh = new GreedyMesher().Mesh(data, null, null, null, null, null, null).Cutout;
        var verts = mesh.Vertices;
        Assert.Equal(3 * 4 * 4, verts.Count);
        for (int q = 0; q < verts.Count / 4; q++)
        {
            var quad = ChunkQuad.Pack(CollectionsMarshal.AsSpan(verts).Slice(4 * q, 4), mesh.Blocks[q]);
            Assert.Equal(mesh.Blocks[q], quad.Block);
            for (int c = 0; c < 4; c++)
            {
                var want = verts[4 * q + c];
                var got = quad.Corner(c);
                Assert.Equal(want.Position, got.Position);
                Assert.Equal(want.Normal, got.Normal);
                Assert.Equal(want.Color, got.Color);
            }
        }
    }

    [Fact]
    public void Generated_plants_grow_on_solid_ground()
    {
        var generator = new HeartWorldGenerator(1337);
        int plants = 0;
        // Around the plains spawn (as GenerationChecksum), surface layers.
        for (int y = 2; y <= 6; y++)
        for (int x = 568; x <= 571; x++)
        for (int z = 33; z <= 35; z++)
        {
            var data = new ChunkData();
            generator.Generate(data, new ChunkPosition(x, y, z));
            for (int lz = 0; lz < ChunkData.Size; lz++)
            for (int ly = 1; ly < ChunkData.Size; ly++)
            for (int lx = 0; lx < ChunkData.Size; lx++)
            {
                if (!BlockRegistry.Get(data.Get(lx, ly, lz)).IsCross) continue;
                plants++;
                Assert.True(BlockRegistry.Get(data.Get(lx, ly - 1, lz)).IsFullCube);
            }
        }
        Assert.True(plants > 0, "some plants grow around spawn");
    }

    [Fact]
    public void Plants_suit_their_ground()
    {
        static IEnumerable<BlockId> On(BlockId ground) =>
            Enumerable.Range(0, 4000).Select(i => ContinentTerrain.Plant(ground, i, 300, -i * 7, 0.7f, 99))
                      .Where(b => b != BlockId.Air).Distinct();

        Assert.Contains(BlockId.TallGrass, On(BlockId.Grass));
        Assert.All(On(BlockId.Sand), b => Assert.Equal(BlockId.DryGrass, b));
        Assert.All(On(BlockId.Rock), b => Assert.True(b is BlockId.Pebbles or BlockId.MossyPebbles));
        Assert.Empty(On(BlockId.Stone));
        Assert.Empty(On(BlockId.Water));
    }

    private static string Resource(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "ClearSkies.Game", "Resources", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(name);
    }
}
