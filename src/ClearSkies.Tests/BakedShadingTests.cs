using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>The mesher bakes what the fragment shader's lighting reads around each face (GreedyMesher.ShadingAt): it
/// must match the shader's own working from the 3³ solid mask, and a merged quad's cells must all share it.</summary>
public class BakedShadingTests
{
    private static ChunkData Terrain()
    {
        var data = new ChunkData();
        var rng = new Random(7);
        for (int x = 0; x < ChunkData.Size; x++)
        for (int z = 0; z < ChunkData.Size; z++)
        {
            int h = 8 + (int)(4 * MathF.Sin(x * 0.4f) + 3 * MathF.Cos(z * 0.3f)) + rng.Next(0, 3);
            for (int y = 0; y < h; y++) data.Set(x, y, z, y < h - 1 ? BlockId.Stone : BlockId.Grass);
        }
        // Overhangs, pillars and a glass pane (doesn't block light) for corners in every direction.
        for (int x = 10; x < 20; x++) for (int z = 10; z < 14; z++) data.Set(x, 20, z, BlockId.Stone);
        for (int y = 10; y < 25; y++) data.Set(5, y, 25, BlockId.Wood);
        for (int y = 12; y < 16; y++) data.Set(15, y, 5, BlockId.Glass);
        return data;
    }

    [Fact]
    public void BakedShadingMatchesTheShadersMaskWorking()
    {
        var data = Terrain();
        // Past the chunk reads as air (no neighbours given).
        bool Solid(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < ChunkData.Size && y < ChunkData.Size
                                           && z < ChunkData.Size && BlockRegistry.Get(data.Get(x, y, z)).BlocksLight;
        var normals = new[] { new Vector3D<int>(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) };
        int baked = 0;
        for (int x = 0; x < ChunkData.Size; x++)
        for (int y = 0; y < ChunkData.Size; y++)
        for (int z = 0; z < ChunkData.Size; z++)
        foreach (var n in normals)
        {
            uint s = GreedyMesher.ShadingAt(data, x, y, z, n);
            var air = new Vector3D<int>(x, y, z) + n;
            Assert.NotEqual(0u, s & GreedyMesher.ShadingBaked);
            baked++;

            // The shader's way: a 27-bit mask (bit (dx+1) + 3(dy+1) + 9(dz+1)) around the air cell.
            uint m = 0;
            for (int dz = -1; dz <= 1; dz++) for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                if (Solid(air.X + dx, air.Y + dy, air.Z + dz)) m |= 1u << ((dx + 1) + 3 * (dy + 1) + 9 * (dz + 1));
            bool M(Vector3D<int> d) => ((m >> ((d.X + 1) + 3 * (d.Y + 1) + 9 * (d.Z + 1))) & 1) == 1;
            Vector3D<int> T, B;
            if (n.X != 0) { T = new(0, 1, 0); B = new(0, 0, 1); }
            else if (n.Y != 0) { T = new(1, 0, 0); B = new(0, 0, 1); }
            else { T = new(1, 0, 0); B = new(0, 1, 0); }
            bool sTm = M(-T), sTp = M(T), sBm = M(-B), sBp = M(B);
            bool sMM = M(-T - B), sPM = M(T - B), sMP = M(-T + B), sPP = M(T + B);
            bool oTm = !sTm && M(-T - n), oTp = !sTp && M(T - n), oBm = !sBm && M(-B - n), oBp = !sBp && M(B - n);
            bool oMM = (oTm || oBm) && !sMM && M(-T - B - n), oPM = (oTp || oBm) && !sPM && M(T - B - n);
            bool oMP = (oTm || oBp) && !sMP && M(-T + B - n), oPP = (oTp || oBp) && !sPP && M(T + B - n);
            bool[] want = { sTm, sTp, sBm, sBp, sMM, sPM, sMP, sPP, oTm, oTp, oBm, oBp, oMM, oPM, oMP, oPP };
            for (int i = 0; i < 16; i++) Assert.Equal(want[i], ((s >> i) & 1) == 1);
        }
        Assert.True(baked > 1000);
    }

    [Fact]
    public void BorderShadingReadsTheFaceNeighbour()
    {
        // A stone ledge in the +X neighbour, beside the air in front of a top face at this chunk's +X edge: the face's
        // +T (+X) corner is shaded only if the neighbour is read.
        var data = new ChunkData();
        data.Set(31, 5, 10, BlockId.Stone);
        var east = new ChunkData();
        east.Set(0, 6, 10, BlockId.Stone);
        var up = new Vector3D<int>(0, 1, 0);
        uint alone = GreedyMesher.ShadingAt(data, 31, 5, 10, up);
        uint withEast = GreedyMesher.ShadingAt(new GreedyMesher.Neighbourhood(data, pX: east), 31, 5, 10, up);
        Assert.Equal(0u, alone & 2u);    // sTp: +T reads as air
        Assert.Equal(2u, withEast & 2u); // the neighbour's block
    }

    [Fact]
    public void AMergedQuadsCellsAllShareItsShading()
    {
        var data = Terrain();
        var mesh = new GreedyMesher().Mesh(data, null, null, null, null, null, null).Opaque;
        Assert.Equal(mesh.Vertices.Count / 4, mesh.Shading.Count);
        int checkedCells = 0, bakedQuads = 0;
        for (int q = 0; q < mesh.Shading.Count; q++)
        {
            var v = mesh.Vertices.GetRange(4 * q, 4);
            var quad = ChunkQuad.Pack(v.ToArray(), mesh.Blocks[q], mesh.Shading[q]);
            Assert.Equal(mesh.Shading[q], quad.Shading); // survives packing
            if ((mesh.Shading[q] & GreedyMesher.ShadingBaked) != 0) bakedQuads++;

            var n = v[0].Normal;
            var normal = new Vector3D<int>((int)MathF.Round(n.X), (int)MathF.Round(n.Y), (int)MathF.Round(n.Z));
            var min = Vector3D.Min(Vector3D.Min(v[0].Position, v[1].Position), Vector3D.Min(v[2].Position, v[3].Position));
            var max = Vector3D.Max(Vector3D.Max(v[0].Position, v[1].Position), Vector3D.Max(v[2].Position, v[3].Position));
            // The blocks behind the face: on the plane's low side for a positive normal.
            for (int x = (int)min.X; x < System.Math.Max((int)max.X, (int)min.X + 1); x++)
            for (int y = (int)min.Y; y < System.Math.Max((int)max.Y, (int)min.Y + 1); y++)
            for (int z = (int)min.Z; z < System.Math.Max((int)max.Z, (int)min.Z + 1); z++)
            {
                int bx = x - (normal.X > 0 ? 1 : 0), by = y - (normal.Y > 0 ? 1 : 0), bz = z - (normal.Z > 0 ? 1 : 0);
                Assert.Equal(mesh.Shading[q], GreedyMesher.ShadingAt(data, bx, by, bz, normal));
                checkedCells++;
            }
        }
        Assert.True(checkedCells > mesh.Shading.Count);
        Assert.True(bakedQuads > 0);
    }
}
