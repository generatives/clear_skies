using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;
using Xunit;

namespace ClearSkies.Tests;

public class IslandTests
{
    [Fact]
    public void Column_layers_cover_every_generated_span()
    {
        var generator = new IslandWorldGenerator(1337);
        var (gx, _, gz) = generator.GroundNear(0f, 0f);
        int cx0 = (int)MathF.Floor(gx / (float)ChunkData.Size), cz0 = (int)MathF.Floor(gz / (float)ChunkData.Size);
        const int MinChunkY = IslandField.WorldBottom / ChunkData.Size;
        int spans = 0;
        // Across a landmass's middle and out past its edge.
        for (int cx = cx0; cx < cx0 + 120; cx += 7)
        {
            ulong layers = generator.ColumnLayers(cx, cz0, MinChunkY);
            generator.Prepare(cx, cz0);
            for (int lz = 0; lz < ChunkData.Size; lz += 5)
            for (int lx = 0; lx < ChunkData.Size; lx += 5)
            foreach (var (lo, hi) in generator.Spans(lx, lz).ToArray())
            {
                spans++;
                for (int y = lo; y <= hi; y += 8)
                    Assert.NotEqual(0UL, layers & (1UL << ((int)MathF.Floor(y / (float)ChunkData.Size) - MinChunkY)));
            }
        }
        Assert.True(spans > 0);
    }

    [Fact]
    public void Landmass_cores_are_whole_and_the_sky_between_is_open()
    {
        var (x, z) = IslandField.NearestLandmassCore(1337, 0f, 0f);
        Assert.Equal(1f, IslandField.Coverage(1337, IslandField.Landmass, x, z));
        // Somewhere within a few landmasses' reach there is open sky, with no landmass at all.
        bool open = false;
        for (int i = 0; i < 400 && !open; i++)
            open = IslandField.Coverage(1337, IslandField.Landmass, x + (i % 20) * 1500f, z + (i / 20) * 1500f) == 0f;
        Assert.True(open);
    }
}
