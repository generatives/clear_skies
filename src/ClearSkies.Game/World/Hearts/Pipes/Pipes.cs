using System.Runtime.Intrinsics;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Surface water on a <see cref="CoarseLandmass"/> by the pipes rule: each face (each link, see CoarseLandmass) carries a flow that remembers itself from step to
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

    /// <summary>Each node's water (the landmass model's, see <see cref="CoarseLandmass.Depth"/>).</summary>
    public float[] Depth => _coarse.Depth;
    /// <summary>Each link's flow over the last step (the landmass model's, see <see cref="CoarseLandmass.Flow"/>).</summary>
    public float[] Flow { get => _coarse.Flow; private set => _coarse.Flow = value; }
    private float[] _limited; // where Apply writes the flows it limits, then swapped with Flow
    public double Time;
    /// <summary>Time spent in each part of a step, for finding what's slow.</summary>
    public readonly System.Diagnostics.Stopwatch FlowTime = new(), ApplyTime = new(), GroundTime = new(), ErodeTime = new();
    public int Steps;

    private readonly CoarseLandmass _coarse;
    private readonly float[] _level;
    private readonly float _width, _area;
    private readonly int[] _faceStart, _other; // the landmass model's LinkStart and LinkTo
    private readonly float[] _rain, _ratio;

    // Faces, for the flow pass (see Step): each one's nodes (B -1 for a drain), its link from A and the link back (-1 for
    // a drain), the top of its opening and the distance between its nodes; padded to whole vectors.
    private const int Lanes = 8;
    private readonly int _faces;
    private readonly int[] _faceA, _faceB, _faceLink, _faceBack;
    private readonly float[] _faceTop, _faceLength;

    private float _deepest;

    public Pipes(CoarseLandmass coarse)
    {
        _coarse = coarse;
        _width = coarse.Size; _area = coarse.Size * coarse.Size;
        _limited = new float[coarse.Links];
        _ratio = new float[coarse.Nodes];
        _level = new float[coarse.Nodes];
        _faceStart = coarse.LinkStart; _other = coarse.LinkTo;

        // Faces: each link to a lower-numbered node's is that node's face, so one link of each (a drain's only link).
        var faces = new List<(int Node, int Link)>(coarse.Links / 2);
        for (int a = 0; a < coarse.Nodes; a++)
            for (int k = _faceStart[a]; k < _faceStart[a + 1]; k++)
                if (_other[k] < 0 || _other[k] > a) faces.Add((a, k));
        _faces = faces.Count;
        int padded = (_faces + Lanes - 1) / Lanes * Lanes; // (the last lanes read node 0 and are never written)
        _faceA = new int[padded]; _faceB = new int[padded]; _faceLink = new int[padded]; _faceBack = new int[padded];
        _faceTop = new float[padded]; _faceLength = new float[padded];
        Array.Fill(_faceB, -1); Array.Fill(_faceBack, -1); Array.Fill(_faceLength, 1f);
        for (int f = 0; f < _faces; f++)
        {
            var (a, k) = faces[f];
            _faceA[f] = a; _faceB[f] = _other[k]; _faceLink[f] = k;
            _faceBack[f] = coarse.LinkBack[k]; _faceTop[f] = coarse.LinkTop[k]; _faceLength[f] = coarse.LinkLength[k];
        }

        _rain = new float[coarse.Nodes];
        for (int c = 0; c < coarse.Columns; c++) if (coarse.HasLand(c)) _rain[coarse.TopNode(c)] = coarse.Wetness[c];
    }

    /// <summary>Sets each hollow's water to where what runs into it balances what it soaks away, up to where it spills
    /// (so lakes don't have to fill or drain at the rain's pace). A priority flood from the drains finds where each
    /// hollow spills and the way water runs out of every node; the rain is then gathered down those ways (losing Soak
    /// at each node it wets) into the hollows. A hollow fed enough to keep N of its nodes wet holds water over its N
    /// lowest; one fed more than its whole spills the rest on downstream. Water outside hollows (streams) is left as it
    /// is. Returns how many nodes it wets.</summary>
    public int FillHollows()
    {
        var coarse = _coarse;
        var spill = new float[coarse.Nodes];
        var parent = new int[coarse.Nodes];
        var order = new List<int>(coarse.Nodes);
        Array.Fill(spill, float.PositiveInfinity);
        Array.Fill(parent, -1);
        var queue = new PriorityQueue<int, float>();
        for (int n = 0; n < coarse.Nodes; n++)
            for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
                if (_other[k] < 0 && coarse.Floor[n] < spill[n]) { spill[n] = coarse.Floor[n]; queue.Enqueue(n, spill[n]); }
        var done = new bool[coarse.Nodes];
        while (queue.TryDequeue(out int n, out float level))
        {
            if (done[n] || level > spill[n]) continue;
            done[n] = true;
            order.Add(n);
            for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
            {
                int m = _other[k];
                if (m < 0 || done[m]) continue;
                float to = Math.Max(level, coarse.LinkBottom(n, k));
                if (to < spill[m]) { spill[m] = to; parent[m] = n; queue.Enqueue(m, to); }
            }
        }

        // Each hollow: the nodes under its spill level, named by the one its water leaves by (following the ways out
        // while the level stays the same), lowest floor first.
        const float Under = 1e-3f;
        bool Flooded(int n) => !float.IsInfinity(spill[n]) && spill[n] - coarse.Floor[n] > Under;
        var hollow = new int[coarse.Nodes];
        Array.Fill(hollow, -1);
        foreach (int n in order)
        {
            if (!Flooded(n)) continue;
            int p = parent[n];
            hollow[n] = p >= 0 && Flooded(p) && Math.Abs(spill[p] - spill[n]) <= Under ? hollow[p] : n;
        }
        var members = new Dictionary<int, List<int>>();
        foreach (int n in order)
        {
            if (hollow[n] < 0) continue;
            if (!members.TryGetValue(hollow[n], out var list)) members[hollow[n]] = list = new List<int>();
            list.Add(n);
        }
        foreach (var list in members.Values) list.Sort((a, b) => coarse.Floor[a].CompareTo(coarse.Floor[b]));

        // The rain, gathered down the ways out (the reverse of the flood's order reaches each node after all that run into it).
        var gathered = new double[coarse.Nodes];
        double rain = Rain * _area, soak = Soak * _area;
        int wet = 0;
        for (int i = order.Count - 1; i >= 0; i--)
        {
            int n = order[i];
            double into = gathered[n] + rain * _rain[n];
            if (hollow[n] < 0) into = Math.Max(0, into - soak);
            else if (hollow[n] == n)
            {
                var list = members[n];
                int keeps = (int)Math.Min(list.Count, Math.Floor(into / soak));
                float level = keeps >= list.Count ? spill[n] : coarse.Floor[list[keeps]];
                for (int j = 0; j < list.Count; j++)
                {
                    int m = list[j];
                    float full = j < keeps ? Math.Min(level, coarse.Ceiling[m]) - coarse.Floor[m] : 0f;
                    Depth[m] = Math.Max(0f, full);
                    if (full > 0) wet++;
                }
                into = Math.Max(0, into - keeps * soak);
            }
            if (parent[n] >= 0) gathered[parent[n]] += into;
        }
        return wet;
    }

    /// <summary>One step of the whole model; returns its length in seconds.</summary>
    public float Step()
    {
        var coarse = _coarse;
        if (Steps % 16 == 0) _deepest = Deepest();
        float dt = Math.Min(MostStep, Courant * _width / MathF.Sqrt(G * Math.Clamp(_deepest, 0.01f, WaveDepth)));
        float weir = 0.385f * MathF.Sqrt(2 * G), n2 = Manning * Manning;

        // Each face's flow, written to both its links: eight faces at a time (their nodes read one by one, the sums
        // done together), each lane doing exactly what one face alone would.
        FlowTime.Start();
        var flow = Flow; var depth = Depth; var floor = coarse.Floor;
        var faceA = _faceA; var faceB = _faceB; var faceLink = _faceLink; var faceBack = _faceBack;
        var faceTop = _faceTop; var faceLength = _faceLength;
        int faceCount = _faces;
        float waveDepth = WaveDepth, width = coarse.FaceWidth;
        Vector256<float> vWeir = Vector256.Create(weir * width), vWidth = Vector256.Create(width), vWave = Vector256.Create(waveDepth),
                         vGdt = Vector256.Create(G * dt), vGdtN2 = Vector256.Create(G * dt * n2), vG = Vector256.Create(G),
                         vDry = Vector256.Create(1e-4f), vOne = Vector256.Create(1f);
        ForBlocks(faceCount, (first, end) =>
        {
            Span<float> fa = stackalloc float[Lanes], da = stackalloc float[Lanes], fb = stackalloc float[Lanes],
                        db = stackalloc float[Lanes], was = stackalloc float[Lanes], q = stackalloc float[Lanes];
            for (int f0 = first; f0 < end; f0 += Lanes)
            {
                for (int l = 0; l < Lanes; l++)
                {
                    int f = f0 + l, a = faceA[f], b = faceB[f];
                    fa[l] = floor[a]; da[l] = depth[a];
                    fb[l] = b >= 0 ? floor[b] : float.NegativeInfinity; db[l] = b >= 0 ? depth[b] : 0f;
                    was[l] = f < faceCount ? flow[faceLink[f]] : 0f;
                }
                var floorA = Vector256.Create<float>(fa); var floorB = Vector256.Create<float>(fb);
                var upA = floorA + Vector256.Create<float>(da); var upB = floorB + Vector256.Create<float>(db);
                var bottom = Vector256.Max(floorA, floorB);
                var high = Vector256.Max(upA, upB); var low = Vector256.Min(upA, upB);
                var h = Vector256.Min(high, Vector256.Create<float>(faceTop.AsSpan(f0, Lanes))) - bottom;
                if (Vector256.LessThanOrEqualAll(h, vDry)) q.Clear(); // (as most faces are: air, and dry ground)
                else
                {
                    // Pours over the edge from the higher side.
                    var poured = vWeir * h * Vector256.Sqrt(h);
                    poured = Vector256.ConditionalSelect(Vector256.LessThan(upA, upB), -poured, poured);
                    // Else pushed by the slope, held back by friction, no faster than a wave.
                    var deep = Vector256.Min(h, vWave);
                    var old = Vector256.Create<float>(was) / vWidth;
                    var slope = (upB - upA) / Vector256.Create<float>(faceLength.AsSpan(f0, Lanes));
                    var next = (old - vGdt * deep * slope) / (vOne + vGdtN2 * Vector256.Abs(old) / (deep * deep * ExactMath.Cbrt(deep)));
                    var most = deep * Vector256.Sqrt(vG * deep);
                    var pushed = Vector256.Min(Vector256.Max(next, -most), most) * vWidth;
                    Vector256.ConditionalSelect(Vector256.LessThanOrEqual(h, vDry), Vector256<float>.Zero, Vector256.ConditionalSelect(Vector256.LessThan(low, bottom), poured, pushed)).CopyTo(q);
                }
                for (int l = 0; l < Lanes && f0 + l < faceCount; l++)
                {
                    int f = f0 + l;
                    flow[faceLink[f]] = q[l];
                    if (faceBack[f] >= 0) flow[faceBack[f]] = -q[l];
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
        var coarse = _coarse;
        // Locals, so the loops keep them in registers.
        var flow = Flow; var depth = Depth; var ratio = _ratio; var rainOn = _rain; var start = _faceStart;
        var other = _other; var limited = _limited;
        float area = _area, rain = Rain * dt;
        ForBlocks(coarse.Nodes, (first, end) =>
        {
            for (int n = first; n < end; n++)
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
        ForBlocks(coarse.Nodes, (first, end) =>
        {
            for (int n = first; n < end; n++)
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
        foreach (float depth in Depth) most = Math.Max(most, depth);
        return most;
    }

    /// <summary>How many nodes each parallel task takes (see ForBlocks).</summary>
    private const int Block = 4096;

    /// <summary>Runs <paramref name="body"/> over nodes 0 to <paramref name="count"/> in parallel, Block at a time (each
    /// call given its first node and the one past its last).</summary>
    private static void ForBlocks(int count, Action<int, int> body) =>
        Parallel.For(0, (count + Block - 1) / Block, block => body(block * Block, Math.Min(count, (block + 1) * Block)));

    /// <summary>The water budget right now, in blocks³ a second: rain on the land, what leaves it over its edges, and
    /// what soaks in.</summary>
    public (double Rain, double Drained, double Soaked, double Stored) Budget()
    {
        var coarse = _coarse;
        double rain = 0, drained = 0, soaked = 0, stored = 0;
        for (int n = 0; n < coarse.Nodes; n++)
        {
            rain += Rain * _rain[n] * _area;
            if (Groundwater != null) soaked += Soaking![n] - Springs![n];
            else if (Depth[n] > 0) soaked += Soak * _area;
            stored += Depth[n] * _area;
        }
        for (int k = 0; k < coarse.Links; k++) if (_other[k] < 0 && Flow[k] > 0) drained += Flow[k];
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
