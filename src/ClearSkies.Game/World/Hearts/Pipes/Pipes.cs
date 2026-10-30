namespace ClearSkies.Game.Generation;

/// <summary>
/// Surface water on a <see cref="Land"/> by the pipes rule: each face (each link, see Land) carries a flow that remembers itself from step to
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

    public float Manning = 0.14f;
    /// <summary>Rain on the wettest land, in blocks a second.</summary>
    public float Rain = 3e-4f;
    /// <summary>What every wet node loses into the ground, in blocks a second.</summary>
    public float Soak = 5e-5f;
    /// <summary>Fraction of the step a shallow-water wave may cross a cell in (the Courant number).</summary>
    public float Courant = 0.9f;
    public float MostStep = 30f;
    /// <summary>Water deeper than this levels out (and runs) as if it were this deep: deep lakes would otherwise set
    /// the step length by how fast a wave crosses them.</summary>
    public float WaveDepth = 1f;

    public readonly float[] Depth;
    /// <summary>Each link's flow over the last step, in blocks³ a second, out of its node (so a face's two links carry
    /// it with opposite signs).</summary>
    public float[] Flow { get; private set; }
    private float[] _limited; // where Apply writes the flows it limits, then swapped with Flow
    public double Time;
    /// <summary>Time spent in each part of a step, for finding what's slow.</summary>
    public readonly System.Diagnostics.Stopwatch FlowTime = new(), ApplyTime = new(), GroundTime = new(), ErodeTime = new();
    public int Steps;

    private readonly Land _land;
    private readonly float[] _level;
    private readonly float _width, _area;
    private readonly int[] _faceStart, _other; // the land's LinkStart and LinkTo
    private readonly float[] _rain, _ratio;
    private float _deepest;

    public Pipes(Land land)
    {
        _land = land;
        _width = land.Size; _area = land.Size * land.Size;
        Depth = new float[land.Nodes];
        Flow = new float[land.Links];
        _limited = new float[land.Links];
        _ratio = new float[land.Nodes];
        _level = new float[land.Nodes];
        _faceStart = land.LinkStart; _other = land.LinkTo;

        _rain = new float[land.Nodes];
        for (int c = 0; c < land.Columns; c++) if (land.HasLand(c)) _rain[land.TopNode(c)] = land.Wetness[c];
    }

    /// <summary>Sets every hollow full to where it spills (a priority flood from the drains): lakes don't have to fill
    /// or drain at the rain's pace. Water outside hollows (streams) is left as it is. Returns how many nodes are in
    /// hollows.</summary>
    public int FillHollows()
    {
        var land = _land;
        var spill = new float[land.Nodes];
        Array.Fill(spill, float.PositiveInfinity);
        var queue = new PriorityQueue<int, float>();
        for (int n = 0; n < land.Nodes; n++)
            for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
                if (_other[k] < 0 && land.Floor[n] < spill[n]) { spill[n] = land.Floor[n]; queue.Enqueue(n, spill[n]); }
        while (queue.TryDequeue(out int n, out float level))
        {
            if (level > spill[n]) continue;
            for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
            {
                int m = _other[k];
                if (m < 0) continue;
                float to = Math.Max(level, land.LinkBottom(n, k));
                if (to < spill[m]) { spill[m] = to; queue.Enqueue(m, to); }
            }
        }
        int wet = 0;
        for (int n = 0; n < land.Nodes; n++)
        {
            if (float.IsInfinity(spill[n])) continue;
            float full = Math.Min(spill[n], land.Ceiling[n]) - land.Floor[n];
            if (full <= 0) continue;
            Depth[n] = full;
            wet++;
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

        // Each face's flow, worked out by the node on its lower-numbered side (or the node a drain leads from) and written
        // to both its links.
        FlowTime.Start();
        var flow = Flow; var depth = Depth; var floor = land.Floor; var start = _faceStart; var other = _other;
        var back = land.LinkBack; var linkTop = land.LinkTop; var linkLength = land.LinkLength;
        float waveDepth = WaveDepth, width = land.FaceWidth;
        Parallel.For(0, (land.Nodes + 1023) / 1024, block =>
        {
            int end = Math.Min(depth.Length, (block + 1) * 1024);
            for (int a = block * 1024; a < end; a++)
            {
                float floorA = floor[a], upA = floorA + depth[a];
                for (int k = start[a]; k < start[a + 1]; k++)
                {
                    int b = other[k];
                    if (b >= 0 && b < a) continue; // the other side's
                    float bottom = b >= 0 ? Math.Max(floorA, floor[b]) : floorA;
                    float upB = b >= 0 ? floor[b] + depth[b] : float.NegativeInfinity;
                    float high = Math.Max(upA, upB), low = Math.Min(upA, upB);
                    float h = Math.Min(high, linkTop[k]) - bottom, q;
                    if (h <= 1e-4f) q = 0;
                    else if (low < bottom)
                    {
                        // Pours over the edge from the higher side.
                        q = weir * width * h * MathF.Sqrt(h);
                        if (upA < upB) q = -q;
                    }
                    else
                    {
                        h = Math.Min(h, waveDepth);
                        float old = flow[k] / width, slope = (upB - upA) / linkLength[k];
                        float next = (old - G * dt * h * slope) / (1 + G * dt * n2 * Math.Abs(old) / (h * h * MathF.Cbrt(h)));
                        float most = h * MathF.Sqrt(G * h); // no faster than a wave
                        q = Math.Clamp(next, -most, most) * width;
                    }
                    flow[k] = q;
                    if (b >= 0) flow[back[k]] = -q;
                }
            }
        });

        FlowTime.Stop();
        ApplyTime.Start();
        Apply(dt);
        ApplyTime.Stop();
        GroundTime.Start();
        if (Groundwater != null && Steps % GroundEvery == 0) Seep(dt);
        GroundTime.Stop();
        // Ground moves slowly next to water: worn every ErodeEvery steps, by those steps' time together.
        ErodeTime.Start();
        _erodeTime += dt;
        if (Eroding && Steps % ErodeEvery == 0) { Erode(_erodeTime); _erodeTime = 0; }
        ErodeTime.Stop();
        Time += dt;
        Steps++;
        return dt;
    }

    /// <summary>Moves the water by this step's face flows: no node gives away more than it has, then each node's
    /// depth changes by what flows in and out, rain and soak.</summary>
    private void Apply(float dt)
    {
        var land = _land;
        // Locals, so the loops keep them in registers.
        var flow = Flow; var depth = Depth; var ratio = _ratio; var rainOn = _rain; var start = _faceStart;
        var other = _other; var limited = _limited;
        float area = _area, rain = Rain * dt;
        Parallel.For(0, (land.Nodes + 4095) / 4096, block =>
        {
            int end = Math.Min(depth.Length, (block + 1) * 4096);
            for (int n = block * 4096; n < end; n++)
            {
                float outs = 0;
                for (int k = start[n]; k < start[n + 1]; k++) outs += Math.Max(0f, flow[k]);
                float has = (depth[n] + rain * rainOn[n]) * area;
                ratio[n] = outs * dt > has ? has / (outs * dt) : 1f;
            }
        });
        // Each node's water.
        var soaking = Soaking; var springs = Springs;
        float soak = Soak * dt;
        Parallel.For(0, (land.Nodes + 4095) / 4096, block =>
        {
            int end = Math.Min(depth.Length, (block + 1) * 4096);
            for (int n = block * 4096; n < end; n++)
            {
                // Each link's flow cut down by the share its giving side can give (both links of a face come out the same).
                float net = 0, mine = ratio[n];
                for (int k = start[n]; k < start[n + 1]; k++)
                {
                    float q = flow[k]; // out of n
                    if (q > 0) q *= mine; else if (q < 0) q *= ratio[other[k]];
                    net -= q;
                    limited[k] = q;
                }
                float d = depth[n] + net * dt / area + rain * rainOn[n];
                if (soaking != null)
                {
                    // Soaks into the ground under it, and springs come up where the ground is full.
                    float soaks = Math.Clamp(d, 0f, soak);
                    soaking[n] = soaks * area / dt;
                    depth[n] = Math.Max(0f, d - soaks) + springs![n] * dt / area;
                }
                else depth[n] = d > 0 ? Math.Max(0f, d - soak) : 0f;
            }
        });
        (Flow, _limited) = (_limited, Flow);
    }

    /// <summary>Sets the public number or switch called <paramref name="name"/> (any case, dashes ignored: "slide-rate"
    /// is SlideRate) to <paramref name="value"/>. False if there's none by that name. With no pipes, only checks.</summary>
    public static bool Set(Pipes? pipes, string name, string value)
    {
        var field = typeof(Pipes).GetFields().FirstOrDefault(f => !f.IsInitOnly && !f.IsLiteral &&
            string.Equals(f.Name, name.Replace("-", ""), StringComparison.OrdinalIgnoreCase));
        if (field == null || !(field.FieldType == typeof(float) || field.FieldType == typeof(int) || field.FieldType == typeof(bool))) return false;
        object set = Convert.ChangeType(value, field.FieldType, System.Globalization.CultureInfo.InvariantCulture);
        if (pipes != null) field.SetValue(pipes, set);
        return true;
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
        for (int k = 0; k < land.Links; k++) if (_other[k] < 0 && Flow[k] > 0) drained += Flow[k];
        return (rain, drained, soaked, stored);
    }

    /// <summary>How much water runs through node n (the larger of what comes in and what goes out), in blocks³ a second.</summary>
    public float Through(int n)
    {
        float ins = 0, outs = 0;
        for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
        {
            float q = Flow[k];
            if (q > 0) outs += q; else ins -= q;
        }
        return Math.Max(ins, outs);
    }
}
