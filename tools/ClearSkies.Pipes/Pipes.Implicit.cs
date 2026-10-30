namespace ClearSkies.Pipes;

/// <summary>
/// The same face rule, taken with the water levels at the end of the step instead of the start (semi-implicit, after
/// Casulli): a step can be far longer than a wave takes to cross a cell, since each node's level is found together with
/// its neighbours'. The levels are found by sweeps: each node in turn sets its level to balance what its faces carry at
/// its neighbours' latest levels (Gauss-Seidel, overshooting by Relax), back and forth until they stop moving. Water
/// pouring over an edge takes only the higher side's level. Afterwards the flows are worked out from the levels and
/// applied as in an explicit step, so no water is made or lost.
/// </summary>
public sealed partial class Pipes
{
    /// <summary>Step length for StepImplicit, in seconds.</summary>
    public float ImplicitStep = 60f;
    public float Relax = 1.5f;
    /// <summary>Sweeps stop once no level moves more than this many blocks, or after MostSweeps.</summary>
    public float Tolerance = 1e-3f;
    public int MostSweeps = 400;
    public int LastSweeps;

    private float[]? _push, _couple, _diag, _rhs; // per face: flow at no level difference, and per block of difference
    private byte[]? _kind; // per face: 0 shut, 1 both sides' levels, 2 pours over from A, 3 from B

    public float StepImplicit()
    {
        var land = _land;
        float dt = ImplicitStep, n2 = Manning * Manning, weir = 0.385f * MathF.Sqrt(2 * G) * _width;
        _push ??= new float[land.Faces]; _couple ??= new float[land.Faces]; _kind ??= new byte[land.Faces];
        _diag ??= new float[land.Nodes]; _rhs ??= new float[land.Nodes];
        var push = _push; var couple = _couple; var kind = _kind; var diag = _diag; var rhs = _rhs;

        // Each face's flow as push - couple * (level B - level A), or pouring over its edge.
        Parallel.For(0, (land.Faces + 4095) / 4096, block =>
        {
            int end = Math.Min(land.Faces, (block + 1) * 4096);
            for (int f = block * 4096; f < end; f++)
            {
                int a = land.FaceA[f], b = land.FaceB[f];
                float bottom = land.FaceBottom[f];
                float upA = land.Floor[a] + Depth[a], upB = b >= 0 ? land.Floor[b] + Depth[b] : float.NegativeInfinity;
                float high = Math.Max(upA, upB), low = Math.Min(upA, upB);
                float h = Math.Min(high, land.FaceTop[f]) - bottom;
                if (h <= 1e-4f) { kind[f] = 0; push[f] = couple[f] = 0; Flow[f] = 0; continue; }
                if (low < bottom)
                {
                    // Pours over: flow = couple * (higher level - bottom), from the higher side only.
                    kind[f] = (byte)(upA >= upB ? 2 : 3);
                    couple[f] = weir * MathF.Sqrt(h);
                    push[f] = 0;
                    continue;
                }
                float old = Flow[f] / _width;
                float friction = 1 + G * dt * n2 * Math.Abs(old) / MathF.Pow(h, 7f / 3f);
                kind[f] = 1;
                push[f] = _width * old / friction;
                couple[f] = _width * G * dt * h / (_width * friction);
            }
        });

        // Each node's balance: area * level + dt * (what its faces carry away) = area * level now + dt * (rain + what
        // pours in).
        Parallel.For(0, (land.Nodes + 4095) / 4096, block =>
        {
            int end = Math.Min(land.Nodes, (block + 1) * 4096);
            for (int n = block * 4096; n < end; n++)
            {
                float level = land.Floor[n] + Depth[n];
                double d = _area, r = _area * level + dt * _area * Rain * _rain[n];
                for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
                {
                    int f = _faceOf[k];
                    bool isA = land.FaceA[f] == n;
                    switch (kind[f])
                    {
                        case 1:
                            d += dt * couple[f];
                            r += isA ? -dt * push[f] : dt * push[f];
                            break;
                        case 2 when isA:
                        case 3 when !isA:
                            d += dt * couple[f];
                            r += dt * couple[f] * land.FaceBottom[f];
                            break;
                        case 2:
                        case 3:
                        {
                            int from = isA ? land.FaceB[f] : land.FaceA[f];
                            r += dt * couple[f] * Math.Max(0f, land.Floor[from] + Depth[from] - land.FaceBottom[f]);
                            break;
                        }
                    }
                }
                diag[n] = (float)d; rhs[n] = (float)r;
                _level[n] = level;
            }
        });

        // Sweeps, alternating direction; blocks of nodes in parallel. A node's level is never under its floor.
        int sweeps = 0;
        for (; sweeps < MostSweeps; sweeps++)
        {
            bool forward = (sweeps & 1) == 0;
            float moved = 0;
            object gate = new();
            Parallel.For(0, (land.Nodes + 2047) / 2048, () => 0f, (block, _, most) =>
            {
                int start = block * 2048, end = Math.Min(land.Nodes, start + 2048);
                for (int i = 0; i < end - start; i++)
                {
                    int n = forward ? start + i : end - 1 - i;
                    double r = rhs[n];
                    for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
                    {
                        int f = _faceOf[k];
                        if (kind[f] != 1) continue;
                        int m = land.FaceA[f] == n ? land.FaceB[f] : land.FaceA[f];
                        r += dt * couple[f] * _level[m];
                    }
                    float next = (float)(r / diag[n]);
                    next = Math.Max(land.Floor[n], _level[n] + Relax * (next - _level[n]));
                    most = Math.Max(most, Math.Abs(next - _level[n]));
                    _level[n] = next;
                }
                return most;
            }, most => { lock (gate) moved = Math.Max(moved, most); });
            if (moved < Tolerance) { sweeps++; break; }
        }
        LastSweeps = sweeps;

        // The flows at those levels.
        Parallel.For(0, (land.Faces + 4095) / 4096, block =>
        {
            int end = Math.Min(land.Faces, (block + 1) * 4096);
            for (int f = block * 4096; f < end; f++)
            {
                int a = land.FaceA[f], b = land.FaceB[f];
                Flow[f] = kind[f] switch
                {
                    1 => push[f] - couple[f] * (_level[b] - _level[a]),
                    2 => couple[f] * Math.Max(0f, _level[a] - land.FaceBottom[f]),
                    3 => -couple[f] * Math.Max(0f, _level[b] - land.FaceBottom[f]),
                    _ => 0f,
                };
            }
        });
        Apply(dt);
        Time += dt;
        Steps++;
        return dt;
    }
}
