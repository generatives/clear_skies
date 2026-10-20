using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;

namespace ClearSkies.Game;

/// <summary>
/// A hash of a fixed set of generated chunks, with a fixed probe seed: two machines whose world generation differs (a
/// different build, a floating-point difference) would disagree about every chunk they didn't receive from the host,
/// so the host refuses a client whose checksum doesn't match its own.
/// </summary>
public static class GenerationChecksum
{
    private const ulong ProbeSeed = 1337;

    public static ulong Compute()
    {
        var generator = new HeartWorldGenerator(ProbeSeed);
        ulong hash = 14695981039346656037UL;
        var data = new ChunkData();
        // Around the plains spawn, 18 km east: surface, underground and sky.
        for (int y = -2; y <= 6; y += 2)
        for (int x = 569; x <= 570; x++)
        {
            data = new ChunkData();
            generator.Generate(data, new ChunkPosition(x, y, 34));
            hash = hash * 31 + DescriptionHash.Of(data.BlocksAsBytes());
        }
        return hash;
    }
}
