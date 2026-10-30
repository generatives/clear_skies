namespace ClearSkies.Pipes;

/// <summary>
/// Surface water on a <see cref="Land"/> by the pipes rule: each face carries a flow that remembers itself from step to
/// step, pushed by the difference in water level across it and held back by friction (the local inertial form of
/// shallow water, Bates et al. 2010):
///
///   new flow = (old flow - g dt depth slope) / (1 + g dt n² |old flow| / depth^(7/3))
///
/// with depth the water over the face's bottom on the higher side and n Manning's roughness. Where the lower side's
/// water is under the face's bottom (a ledge, a waterfall, the land's edge) water pours over it as over a weir instead.
/// Rain falls on each column's top node, every wet node loses Soak a second, and no face gives away more than its node
/// holds. Lengths in blocks (a metre), times in seconds.
/// </summary>
public sealed partial class Pipes
{
    public const float G = 9.81f;

    public float Manning = 0.035f;
    /// <summary>Rain on the wettest land, in blocks a second.</summary>
    public float Rain = 3e-4f;
    /// <summary>What every wet node loses into the ground, in blocks a second.</summary>
    public float Soak = 5e-5f;
    /// <summary>Fraction of the step a shallow-water wave may cross a cell in (the Courant number).</summary>
    public float Courant = 0.7f;
    public float MostStep = 30f;
    /// <summary>Water deeper than this levels out (and runs) as if it were this deep: deep lakes would otherwise set
    /// the step length by how fast a wave crosses them.</summary>
    public float WaveDepth = 2f;

    public readonly float[] Depth, Flow;
    public double Time;
    public int Steps;

    private readonly Land _land;
    private readonly float[] _level;
    private readonly float _width, _area;
    private readonly int[] _faceStart, _faceOf; // per node, its faces (CSR)
    private readonly float[] _rain, _ratio;
    private float _deepest;

    public Pipes(Land land)
    {
        _land = land;
        _width = land.Size; _area = land.Size * land.Size;
        Depth = new float[land.Nodes];
        Flow = new float[land.Faces];
        _ratio = new float[land.Nodes];
        _level = new float[land.Nodes];

        var count = new int[land.Nodes + 1];
        for (int f = 0; f < land.Faces; f++) { count[land.FaceA[f] + 1]++; if (land.FaceB[f] >= 0) count[land.FaceB[f] + 1]++; }
        for (int n = 0; n < land.Nodes; n++) count[n + 1] += count[n];
        _faceStart = (int[])count.Clone();
        _faceOf = new int[count[land.Nodes]];
        var at = (int[])count.Clone();
        for (int f = 0; f < land.Faces; f++) { _faceOf[at[land.FaceA[f]]++] = f; if (land.FaceB[f] >= 0) _faceOf[at[land.FaceB[f]]++] = f; }

        _rain = new float[land.Nodes];
        for (int c = 0; c < land.Columns; c++) if (land.HasLand(c)) _rain[land.TopNode(c)] = land.Wetness[c];
    }

    /// <summary>Starts every hollow full to where it spills (a priority flood from the drains): lakes don't have to fill
    /// at the rain's pace. Returns how many nodes start wet.</summary>
    public int FillHollows()
    {
        var land = _land;
        var spill = new float[land.Nodes];
        Array.Fill(spill, float.PositiveInfinity);
        var queue = new PriorityQueue<int, float>();
        for (int f = 0; f < land.Faces; f++)
            if (land.FaceB[f] < 0 && land.Floor[land.FaceA[f]] < spill[land.FaceA[f]])
            { spill[land.FaceA[f]] = land.Floor[land.FaceA[f]]; queue.Enqueue(land.FaceA[f], spill[land.FaceA[f]]); }
        while (queue.TryDequeue(out int n, out float level))
        {
            if (level > spill[n]) continue;
            for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
            {
                int f = _faceOf[k], m = land.FaceA[f] == n ? land.FaceB[f] : land.FaceA[f];
                if (m < 0) continue;
                float to = Math.Max(level, land.FaceBottom[f]);
                if (to < spill[m]) { spill[m] = to; queue.Enqueue(m, to); }
            }
        }
        int wet = 0;
        for (int n = 0; n < land.Nodes; n++)
        {
            if (float.IsInfinity(spill[n])) continue;
            Depth[n] = Math.Max(0f, Math.Min(spill[n], land.Ceiling[n]) - land.Floor[n]);
            if (Depth[n] > 0) wet++;
        }
        return wet;
    }

    /// <summary>One step of the whole model; returns its length in seconds.</summary>
    public float Step()
    {
        var land = _land;
        if (Steps % 16 == 0) _deepest = Deepest();
        float dt = Math.Min(MostStep, Courant * _width / MathF.Sqrt(G * Math.Clamp(_deepest, 0.01f, WaveDepth)));
        float weir = 0.385f * MathF.Sqrt(2 * G), n2 = Manning * Manning;

        // Each face's flow.
        Parallel.For(0, (land.Faces + 4095) / 4096, block =>
        {
            int end = Math.Min(land.Faces, (block + 1) * 4096);
            for (int f = block * 4096; f < end; f++)
            {
                int a = land.FaceA[f], b = land.FaceB[f];
                float bottom = land.FaceBottom[f], width = land.FaceWidth[f];
                float upA = land.Floor[a] + Depth[a], upB = b >= 0 ? land.Floor[b] + Depth[b] : float.NegativeInfinity;
                float high = Math.Max(upA, upB), low = Math.Min(upA, upB);
                float h = Math.Min(high, land.FaceTop[f]) - bottom;
                if (h <= 1e-4f) { Flow[f] = 0; continue; }
                if (low < bottom)
                {
                    // Pours over the edge from the higher side.
                    float q = weir * width * h * MathF.Sqrt(h);
                    Flow[f] = upA >= upB ? q : -q;
                    continue;
                }
                h = Math.Min(h, WaveDepth);
                float old = Flow[f] / width, slope = (upB - upA) / land.FaceLength[f];
                float next = (old - G * dt * h * slope) / (1 + G * dt * n2 * Math.Abs(old) / MathF.Pow(h, 7f / 3f));
                float most = h * MathF.Sqrt(G * h); // no faster than a wave
                Flow[f] = Math.Clamp(next, -most, most) * width;
            }
        });

        Apply(dt);
        if (Groundwater != null) Seep(dt);
        // Ground moves slowly next to water: worn every ErodeEvery steps, by those steps' time together.
        _erodeTime += dt;
        if (Eroding && Steps % ErodeEvery == 0) { Erode(_erodeTime); _erodeTime = 0; }
        Time += dt;
        Steps++;
        return dt;
    }

    /// <summary>Moves the water by this step's face flows: no node gives away more than it has, then each node's
    /// depth changes by what flows in and out, rain and soak.</summary>
    private void Apply(float dt)
    {
        var land = _land;
        Parallel.For(0, (land.Nodes + 4095) / 4096, block =>
        {
            int end = Math.Min(land.Nodes, (block + 1) * 4096);
            for (int n = block * 4096; n < end; n++)
            {
                float outs = 0;
                for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
                {
                    int f = _faceOf[k];
                    float q = land.FaceA[f] == n ? Flow[f] : -Flow[f];
                    if (q > 0) outs += q;
                }
                float has = (Depth[n] + Rain * _rain[n] * dt) * _area;
                _ratio[n] = outs * dt > has ? has / (outs * dt) : 1f;
            }
        });
        Parallel.For(0, (land.Faces + 4095) / 4096, block =>
        {
            int end = Math.Min(land.Faces, (block + 1) * 4096);
            for (int f = block * 4096; f < end; f++)
            {
                float q = Flow[f];
                if (q > 0) Flow[f] = q * _ratio[land.FaceA[f]];
                else if (q < 0) Flow[f] = q * _ratio[land.FaceB[f]];
            }
        });

        // Each node's water.
        Parallel.For(0, (land.Nodes + 4095) / 4096, block =>
        {
            int end = Math.Min(land.Nodes, (block + 1) * 4096);
            for (int n = block * 4096; n < end; n++)
            {
                float net = 0;
                for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
                {
                    int f = _faceOf[k];
                    net += land.FaceA[f] == n ? -Flow[f] : Flow[f];
                }
                float d = Depth[n] + dt * (net / _area + Rain * _rain[n]);
                if (Groundwater != null)
                {
                    // Soaks into the ground under it, and springs come up where the ground is full.
                    float soaks = Math.Clamp(d, 0f, Soak * dt);
                    Soaking![n] = soaks * _area / dt;
                    Depth[n] = Math.Max(0f, d - soaks) + Springs![n] * dt / _area;
                }
                else Depth[n] = d > 0 ? Math.Max(0f, d - Soak * dt) : 0f;
            }
        });
    }

    private float Deepest()
    {
        float most = 0;
        object gate = new();
        Parallel.For(0, (_land.Nodes + 65535) / 65536, () => 0f, (block, _, local) =>
        {
            int end = Math.Min(_land.Nodes, (block + 1) * 65536);
            for (int n = block * 65536; n < end; n++) local = Math.Max(local, Depth[n]);
            return local;
        }, local => { lock (gate) most = Math.Max(most, local); });
        return most;
    }

    /// <summary>The water budget right now, in blocks³ a second: rain on the land, what leaves it over its edges, and
    /// what soaks in.</summary>
    public (double Rain, double Drained, double Soaked, double Stored) Budget()
    {
        var land = _land;
        double rain = 0, drained = 0, soaked = 0, stored = 0;
        for (int n = 0; n < land.Nodes; n++)
        {
            rain += Rain * _rain[n] * _area;
            if (Groundwater != null) soaked += Soaking![n] - Springs![n];
            else if (Depth[n] > 0) soaked += Soak * _area;
            stored += Depth[n] * _area;
        }
        for (int f = 0; f < land.Faces; f++) if (land.FaceB[f] < 0 && Flow[f] > 0) drained += Flow[f];
        return (rain, drained, soaked, stored);
    }

    /// <summary>How much water runs through node n (the larger of what comes in and what goes out), in blocks³ a second.</summary>
    public float Through(int n)
    {
        var land = _land;
        float ins = 0, outs = 0;
        for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
        {
            int f = _faceOf[k];
            float q = land.FaceA[f] == n ? Flow[f] : -Flow[f];
            if (q > 0) outs += q; else ins -= q;
        }
        return Math.Max(ins, outs);
    }
}
