using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Generation;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Generates a floating-island sky world.
/// Islands are centred around world Y=12 and Y=108 (two layers), spread on XZ by
/// FBM noise so neighbouring chunks produce consistent terrain.
/// Block layers (top→bottom): Grass → Dirt (2 thick) → Stone.
/// </summary>
public sealed class SkyWorldGenerator : IWorldGenerator
{
    // Two island layers in world-space Y.
    private static readonly float[] IslandCentres = { 12f };

    public void Generate(ChunkData data, ChunkPosition pos)
    {
        var origin = pos.WorldOrigin;

        for (int lx = 0; lx < ChunkData.Size; lx++)
        for (int lz = 0; lz < ChunkData.Size; lz++)
        {
            float wx = origin.X + lx;
            float wz = origin.Z + lz;

            // Offsets prevent evaluating at exact integer lattice points (where Hash2 = 0 at origin).
            float presence = Fbm2(wx * 0.012f + 7.3f, wz * 0.012f + 11.7f, 4);
            if (presence < 0.42f) continue;

            float surfaceNoise = Fbm2(wx * 0.025f + 131.7f, wz * 0.025f + 83.1f, 3);
            float thickNoise   = Fbm2(wx * 0.018f + 57.3f,  wz * 0.018f + 212.9f, 2);

            float islandRadius  = (presence - 0.54f) / 0.46f; // 0..1
            float surfaceOffset = surfaceNoise * 4f - 2f;       // ±2 blocks
            float thickness     = 4f + thickNoise * 8f;         // 4..12 blocks deep

            foreach (float centre in IslandCentres)
            {
                float top = centre + surfaceOffset;
                float bot = top - thickness;

                for (int ly = 0; ly < ChunkData.Size; ly++)
                {
                    float wy = origin.Y + ly;
                    if (wy < bot || wy > top) continue;

                    BlockId id;
                    if (wy >= top - 0.5f)
                        id = BlockId.Grass;
                    else if (wy >= top - 2.5f)
                        id = BlockId.Dirt;
                    else
                        id = BlockId.Stone;

                    data.Set(lx, ly, lz, id);
                }
            }
        }
    }

    // ── Noise ────────────────────────────────────────────────────────────────────

    private static float Fbm2(float x, float y, int octaves)
    {
        float value = 0f;
        float amp   = 0.5f;
        float freq  = 1f;
        for (int i = 0; i < octaves; i++)
        {
            value += Smooth2(x * freq, y * freq) * amp;
            freq  *= 2.0f;
            amp   *= 0.5f;
        }
        return value; // approx [0, 1]
    }

    private static float Smooth2(float x, float y)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float fx = x - x0;
        float fy = y - y0;

        // Quintic smoothstep
        fx = fx * fx * fx * (fx * (fx * 6 - 15) + 10);
        fy = fy * fy * fy * (fy * (fy * 6 - 15) + 10);

        float v00 = Hash2(x0,     y0);
        float v10 = Hash2(x0 + 1, y0);
        float v01 = Hash2(x0,     y0 + 1);
        float v11 = Hash2(x0 + 1, y0 + 1);

        return Lerp(Lerp(v00, v10, fx), Lerp(v01, v11, fx), fy);
    }

    private static float Hash2(int x, int y)
    {
        uint h = (uint)(x * 374761393 + y * 668265263);
        h ^= h >> 13;
        h *= 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFF) * (1f / 65535f);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
