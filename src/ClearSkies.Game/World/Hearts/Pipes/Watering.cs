namespace ClearSkies.Game.Generation;

/// <summary>How near water each of a landmass's columns is, and how marshy, for the biomes (see
/// <see cref="CoarseLandmass.Watered"/> and <see cref="CoarseLandmass.Marsh"/>).</summary>
internal static class Watering
{
    /// <summary>Works out <paramref name="coarse"/>'s ColumnWatered and ColumnMarsh from its water and streams.</summary>
    public static void Build(CoarseLandmass coarse)
    {
        var settings = coarse.Settings;
        bool Shows(int n) => coarse.Depth[n] > settings.Damp + CoarseLandmass.Shallow;
        (coarse.ColumnWatered, coarse.ColumnMarsh) =
            Watered(coarse, n => Shows(n) || coarse.Shown[n] > 0f, n => coarse.Floor[n] + coarse.Depth[n], settings);
    }

    /// <summary>0-1 per column: how near water it is, 1 in it, falling off over WaterReach blocks (see
    /// <see cref="Biomes.Wet"/>). Each block climbed on the way out of the water counts as WaterClimb blocks across, so
    /// a river greens its valley floor and not the ridges over it.
    ///
    /// And 0-1 per column: how marshy it is, 1 where it is within MarshReach blocks of water, stands no more than
    /// MarshRise blocks over that water and is flat (rising no more than MarshSlope a block to its neighbours), fading
    /// out past each.</summary>
    internal static (float[] Watered, float[] Marsh) Watered(CoarseLandmass land, Func<int, bool> wet, Func<int, float> level, LandscapeSettings settings)
    {
        var distance = new float[land.Columns];
        var source = new float[land.Columns]; // the level of the water nearest each column
        Array.Fill(distance, float.PositiveInfinity);
        var open = new PriorityQueue<int, float>();
        for (int c = 0; c < land.Columns; c++)
        {
            int t = land.TopNode(c);
            if (t >= 0 && wet(t)) { distance[c] = 0f; source[c] = level(t); open.Enqueue(c, 0f); }
        }
        float far = settings.WaterReach * 4f, diagonal = land.Size * MathF.Sqrt(2f);
        while (open.TryDequeue(out int c, out float d))
        {
            if (d > distance[c] || d > far) continue;
            int i = c % land.Nx, j = c / land.Nx;
            float floor = land.Floor[land.TopNode(c)];
            for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                int ni = i + di, nj = j + dj;
                if ((di | dj) == 0 || ni < 0 || nj < 0 || ni >= land.Nx || nj >= land.Nz) continue;
                int n = ni + land.Nx * nj, t = land.TopNode(n);
                if (t < 0) continue;
                float step = (di != 0 && dj != 0 ? diagonal : land.Size) + settings.WaterClimb * MathF.Max(0f, land.Floor[t] - floor);
                if (d + step < distance[n]) { distance[n] = d + step; source[n] = source[c]; open.Enqueue(n, d + step); }
            }
        }
        var watered = new float[land.Columns];
        var marsh = new float[land.Columns];
        static float Fade(float v) { v = Math.Clamp(v, 0f, 1f); return v * v * (3 - 2 * v); }
        for (int c = 0; c < land.Columns; c++)
        {
            watered[c] = MathF.Exp(-distance[c] / settings.WaterReach);
            int t = land.TopNode(c);
            if (t < 0 || float.IsInfinity(distance[c])) continue;
            int i = c % land.Nx, j = c / land.Nx;
            float floor = land.Floor[t], steepest = 0f;
            for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                int ni = i + di, nj = j + dj;
                if ((di | dj) == 0 || ni < 0 || nj < 0 || ni >= land.Nx || nj >= land.Nz) continue;
                int u = land.TopNode(ni + land.Nx * nj);
                if (u >= 0) steepest = MathF.Max(steepest, MathF.Abs(land.Floor[u] - floor) / (land.Size * MathF.Sqrt(di * di + dj * dj)));
            }
            marsh[c] = Fade(2f - 2f * distance[c] / MarshReach) * Fade(2f - 2f * (floor - source[c]) / MarshRise)
                     * Fade(2f - 2f * steepest / MarshSlope);
        }
        return (watered, marsh);
    }

    private const float MarshReach = 48f, MarshRise = 3f, MarshSlope = 0.25f;
}
