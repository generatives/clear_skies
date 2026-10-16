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
        var generator = new IslandWorldGenerator(ProbeSeed);
        ulong hash = 14695981039346656037UL;
        var data = new ChunkData();
        // At the spawn: surface, underground and sky.
        var (gx, gy, gz) = generator.GroundNear(0f, 0f);
        int cx = (int)MathF.Floor(gx / (float)ChunkData.Size), cy = (int)MathF.Floor(gy / (float)ChunkData.Size), cz = (int)MathF.Floor(gz / (float)ChunkData.Size);
        for (int y = cy - 4; y <= cy + 4; y += 2)
        for (int x = cx; x <= cx + 1; x++)
        {
            data = new ChunkData();
            generator.Generate(data, new ChunkPosition(x, y, cz));
            hash = hash * 31 + DescriptionHash.Of(data.BlocksAsBytes());
        }
        return hash;
    }
}
