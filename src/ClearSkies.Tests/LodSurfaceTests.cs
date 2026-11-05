using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Distant terrain shows the ground's surface block on top, as full detail does: not the dirt under it.</summary>
public class LodSurfaceTests
{
    private const int S = ChunkData.Size;

    [Theory, InlineData(2), InlineData(3)]
    public void ACoarseColumnsTopIsFullDetailsTopBlock(int level)
    {
        int c = 1 << level;
        var generator = new HeartWorldGenerator(1337);
        // The coarse chunk column over the spawn plains (see WorldSpawn).
        int px = 11250 / (S * c), pz = -6250 / (S * c) - 1;
        var tops = new Dictionary<(int U, int V), (int Y, BlockId Block)>();
        for (int py = -256 / (S * c) - 1; py <= 1792 / (S * c); py++)
        {
            var coarse = new ChunkData();
            generator.Generate(coarse, new ChunkPosition(px, py, pz), level);
            for (int v = 0; v < S; v++) for (int u = 0; u < S; u++) for (int w = 0; w < S; w++)
            {
                var id = coarse.Get(u, w, v);
                if (id == BlockId.Air || BlockRegistry.Get(id).IsCross) continue;
                int y = (py * S + w) * c;
                if (!tops.TryGetValue((u, v), out var t) || y > t.Y) tops[(u, v)] = (y, id);
            }
        }

        var fulls = new Dictionary<ChunkPosition, ChunkData>();
        Span<(short Lo, short Hi)> spans = stackalloc (short, short)[16];
        int counted = 0, same = 0, dirt = 0;
        foreach (var ((u, v), (_, top)) in tops)
        {
            if (top == BlockId.Water || BlockRegistry.Get(top).ShowsInnerFaces) continue; // (trees, and water)
            int x = px * S * c + u * c + c / 2, z = pz * S * c + v * c + c / 2;
            int count = generator.SpansAt(x, z, spans);
            if (count == 0) continue;
            int hi = spans[count - 1].Hi;
            var at = new ChunkPosition(x >> 5, hi >> 5, z >> 5);
            if (!fulls.TryGetValue(at, out var full)) generator.Generate(fulls[at] = full = new ChunkData(), at);
            counted++;
            if (full.Get(x & (S - 1), hi & (S - 1), z & (S - 1)) == top) same++;
            if (top == BlockId.Dirt) dirt++;
        }
        Assert.True(counted > S * S / 2, $"only {counted} coarse columns of ground");
        Assert.True(same >= counted * 97 / 100, $"{same} of {counted} coarse tops are full detail's");
        Assert.True(dirt <= counted / 200, $"{dirt} of {counted} coarse tops are dirt");
    }
}
