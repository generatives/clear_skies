using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;
using Xunit;

namespace ClearSkies.Tests;

public class DistanceLatticeTests
{
    /// <summary>A chunk of distant terrain made straight from the lattice has about the same ground as one shrunk from
    /// the full chunks under it.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Distant_terrain_from_the_lattice_matches_shrunk_full_detail(int level)
    {
        var full = new HeartWorldGenerator(1337);
        var direct = new HeartWorldGenerator(1337);
        int n = 1 << level;
        int px = 11250 / (32 * n), pz = -6250 / (32 * n) - 1;
        long solid = 0, differ = 0;
        for (int py = 384 / (32 * n); py <= 896 / (32 * n); py++)
        {
            var pos = new ChunkPosition(px, py, pz);
            var shrunk = new Dictionary<ChunkPosition, ChunkData>();
            for (int j = 0; j < n * n; j++)
            for (int l = 0; l < n; l++)
            {
                var at = new ChunkPosition(pos.X * n + j % n, pos.Y * n + l, pos.Z * n + j / n);
                var chunk = new ChunkData();
                full.Generate(chunk, at);
                if (chunk.HasAnyNonAir()) CoarseTerrain.ShrinkInto(shrunk, at, chunk, majority: true, plants: true);
            }
            for (int step = 2; step <= level; step++)
            {
                var finer = shrunk;
                shrunk = new();
                foreach (var (p, chunk) in finer) CoarseTerrain.ShrinkInto(shrunk, p, chunk, majority: true, plants: true);
            }
            var a = shrunk.GetValueOrDefault(pos) ?? new ChunkData();
            var b = new ChunkData();
            direct.GenerateCoarse(b, pos, level);
            for (int z = 0; z < 32; z++)
            for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                bool sa = Ground(a.Get(x, y, z)), sb = Ground(b.Get(x, y, z));
                if (sa) solid++;
                if (sa != sb) differ++;
            }
        }
        Assert.True(solid > 10000);
        Assert.InRange(differ, 0, solid / 50);

        static bool Ground(BlockId id) => id != BlockId.Air && !BlockRegistry.Get(id).IsCross && !BlockRegistry.Get(id).ShowsInnerFaces;
    }
}
