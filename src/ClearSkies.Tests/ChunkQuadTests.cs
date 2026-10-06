using Xunit;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Tests;

/// <summary>Chunk meshes are uploaded as 8-byte <see cref="ChunkQuad"/>s and rebuilt in vs_chunk: packing each of the
/// mesher's quads and decoding its corners must give back the mesher's vertices, for every face and quad size.</summary>
public class ChunkQuadTests
{
    [Fact]
    public void QuadsDecodeToTheMeshersCorners()
    {
        // Shapes with faces in all six directions and merged quads of many sizes, up to a whole chunk side.
        var data = new ChunkData();
        for (int x = 0; x < ChunkData.Size; x++)
        for (int z = 0; z < ChunkData.Size; z++)
        for (int y = 0; y < 3; y++)
            data.Set(x, y, z, BlockId.Stone);
        for (int x = 4; x < 11; x++)
        for (int y = 3; y < 9; y++)
        for (int z = 20; z < 31; z++)
            data.Set(x, y, z, (x + y) % 3 == 0 ? BlockId.Dirt : BlockId.Grass);
        data.Set(31, 31, 31, BlockId.Stone);

        var verts = new GreedyMesher().Mesh(data, null, null, null, null, null, null).Opaque.Vertices;
        Assert.True(verts.Count > 0 && verts.Count % 4 == 0);
        for (int q = 0; q < verts.Count / 4; q++)
        {
            var quad = ChunkQuad.Pack(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(verts).Slice(4 * q, 4));
            for (int c = 0; c < 4; c++)
            {
                var want = verts[4 * q + c];
                var got = quad.Corner(c);
                Assert.Equal(want.Position, got.Position);
                Assert.Equal(want.Normal, got.Normal);
            }
        }
    }
}
