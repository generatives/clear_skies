namespace ClearSkies.Pipes;

/// <summary>Timing test only: the pipes flow and apply passes over a dense 2D grid of top nodes (caves ignored).</summary>
public static class DenseBench
{
    public static void Run(Land land, int steps, float dt)
    {
        int nx = land.Nx, nz = land.Nz, cols = nx * nz;
        var floor = new float[cols]; var depth = new float[cols]; var rainOn = new float[cols];
        for (int c = 0; c < cols; c++)
        {
            floor[c] = land.HasLand(c) ? land.Floor[land.TopNode(c)] : float.NaN;
            rainOn[c] = land.HasLand(c) ? land.Wetness[c] : 0;
        }
        var flow = new float[4 * cols]; var ratio = new float[cols];
        (int di, int dj)[] dirs = { (1, 0), (0, 1), (1, 1), (-1, 1) };
        float width = land.FaceWidth, size = land.Size, area = size * size;
        float weir = 0.385f * MathF.Sqrt(2 * Pipes.G), n2 = 0.035f * 0.035f;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int s = 0; s < steps; s++)
        {
            Parallel.For(0, nz, j =>
            {
                for (int d = 0; d < 4; d++)
                {
                    var (di, dj) = dirs[d];
                    float len = di != 0 && dj != 0 ? size * 1.41421356f : size;
                    int bj = j + dj;
                    for (int i = 0; i < nx; i++)
                    {
                        int a = i + nx * j, bi = i + di;
                        bool inB = bi >= 0 && bi < nx && bj < nz;
                        int b = inB ? bi + nx * bj : -1;
                        float fa = floor[a], fb = inB ? floor[b] : float.NaN;
                        float q = 0;
                        bool la = !float.IsNaN(fa), lb = !float.IsNaN(fb);
                        if (la || lb)
                        {
                            float upA = la ? fa + depth[a] : float.NegativeInfinity, upB = lb ? fb + depth[b] : float.NegativeInfinity;
                            float bottom = la && lb ? Math.Max(fa, fb) : la ? fa : fb;
                            float high = Math.Max(upA, upB), low = Math.Min(upA, upB), h = high - bottom;
                            if (h > 1e-4f)
                            {
                                if (low < bottom) { q = weir * width * h * MathF.Sqrt(h); if (upA < upB) q = -q; }
                                else
                                {
                                    h = Math.Min(h, 1f);
                                    float old = flow[d * cols + a] / width, slope = (upB - upA) / len;
                                    float next = (old - Pipes.G * dt * h * slope) / (1 + Pipes.G * dt * n2 * Math.Abs(old) / (h * h * MathF.Cbrt(h)));
                                    float most = h * MathF.Sqrt(Pipes.G * h);
                                    q = Math.Clamp(next, -most, most) * width;
                                }
                            }
                        }
                        flow[d * cols + a] = q;
                    }
                }
            });
            Parallel.For(0, nz, j =>
            {
                for (int i = 0; i < nx; i++)
                {
                    int c = i + nx * j; float outs = 0;
                    for (int d = 0; d < 4; d++)
                    {
                        outs += Math.Max(0f, flow[d * cols + c]);
                        int bi = i - dirs[d].di, bj = j - dirs[d].dj;
                        if (bi >= 0 && bi < nx && bj >= 0) outs += Math.Max(0f, -flow[d * cols + bi + nx * bj]);
                    }
                    float has = (depth[c] + 3e-4f * dt * rainOn[c]) * area;
                    ratio[c] = outs * dt > has ? has / (outs * dt) : 1f;
                }
            });
            Parallel.For(0, nz, j =>
            {
                for (int i = 0; i < nx; i++)
                {
                    int c = i + nx * j; float net = 0;
                    if (float.IsNaN(floor[c])) continue;
                    for (int d = 0; d < 4; d++)
                    {
                        var (di, dj) = dirs[d];
                        int oi = i + di, oj = j + dj;
                        float q = flow[d * cols + c];
                        bool inO = oi >= 0 && oi < nx && oj < nz;
                        q *= q > 0 ? ratio[c] : inO ? ratio[oi + nx * oj] : 1f;
                        net -= q;
                        int bi = i - di, bj = j - dj;
                        if (bi >= 0 && bi < nx && bj >= 0)
                        {
                            int b = bi + nx * bj; float r = flow[d * cols + b];
                            r *= r > 0 ? ratio[b] : ratio[c];
                            net += r;
                        }
                    }
                    depth[c] = Math.Max(0f, depth[c] + net * dt / area + 3e-4f * dt * rainOn[c] - 5e-5f * dt);
                }
            });
        }
        double ms = sw.Elapsed.TotalMilliseconds / steps;
        Console.WriteLine($"Dense: {cols:N0} columns, {ms:0.00} ms a step (flow + ratio + apply)");
    }
}
