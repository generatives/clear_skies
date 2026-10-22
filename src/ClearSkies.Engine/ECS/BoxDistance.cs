using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>Distance from a point to the nearest point of a chunk, or of a column of chunks, of any scale.</summary>
internal static class BoxDistance
{
    /// <summary>Horizontally, to column (x, z) of chunks <paramref name="size"/> blocks across.</summary>
    public static float ToColumn(Vector3D<float> from, int x, int z, float size)
    {
        float dx = Gap(from.X, x, size), dz = Gap(from.Z, z, size);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>To chunk (x, y, z) of chunks <paramref name="size"/> blocks across.</summary>
    public static float ToChunk(Vector3D<float> from, int x, int y, int z, float size)
    {
        float dx = Gap(from.X, x, size), dy = Gap(from.Y, y, size), dz = Gap(from.Z, z, size);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static float Gap(float v, int cell, float size) => MathF.Max(0f, MathF.Max(cell * size - v, v - (cell + 1) * size));
}
