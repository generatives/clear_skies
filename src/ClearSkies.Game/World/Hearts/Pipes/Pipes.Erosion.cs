namespace ClearSkies.Game.Generation;

/// <summary>
/// Erosion in the same model (after Mei et al. 2007): water carries ground as sediment. How much a node's water can
/// carry is Carry × its speed × the slope of its surface × its volume; water carrying less than that wears its floor
/// down (Pick of the difference a second), water carrying more drops the rest on its floor (Drop a second). Sediment
/// moves with the water through the faces. Floors change Speedup times as fast as the sediment does, so landscapes
/// form in hours instead of years, and never wear to within MinThickness of their span's bottom. Ground gets harder
/// the deeper it's worn (Pick falls to half Hardening blocks under where it started), so gorges don't cut without end.
/// Loose ground slides: where wear has left a floor standing over a neighbour's more steeply than Slide blocks a block
/// (and than it stood at the start), some of the difference moves down each step, so cuts widen into valleys. Ground
/// that started steeper keeps its slope: cliffs and steep hills stand.
/// </summary>
public sealed partial class Pipes
{
    public bool Eroding;
    /// <summary>How much stronger erosion starts than it ends: it eases from Ease times as strong to 1/Ease times
    /// over the time it's given (see StartEroding), so the big shapes form fast and the end only smooths them.</summary>
    public float Ease = 3f;
    private double _erodeStart, _erodeLength = 1;

    /// <summary>Erodes from now, for <paramref name="seconds"/> (simulated).</summary>
    public void StartEroding(double seconds)
    {
        Eroding = true;
        _erodeStart = Time;
        _erodeLength = Math.Max(1, seconds);
    }

    public int ErodeEvery = 2;
    private float _erodeTime;
    public float Carry = 0.05f, Pick = 0.02f, Drop = 0.001f, Speedup = 900f;
    /// <summary>Slopes count as no steeper than this (falls and cliffs too), so they don't cut without end.</summary>
    public float SteepestCut = 1f;
    /// <summary>Running water's slope counts as at least this, so water crossing a flat still cuts (most where it runs
    /// deepest and fastest, so it gathers there into a channel).</summary>
    public float FlattestCut = 0.15f;
    public float MinThickness = 6f;
    /// <summary>The fastest a floor wears, in blocks a second (with Speedup).</summary>
    public float MostWear = 0.04f;
    public float Hardening = 8f, Slide = 2f, SlideRate = 0.002f, Creep = 1e-5f;

    private float[]? _slid;

    public float[]? Sediment;
    private float[]? _carried, _sedimentRatio, _nextSediment;

    /// <summary>Standing water wears its bed: Scour blocks a second per block of water over it (up to ScourDepth),
    /// slowing as it's worn deeper (see Hardening). What it wears is gone, not carried: in still water it would only
    /// settle back. So hollows where water stands deepen into bowls.</summary>
    public float Scour = 5e-4f, ScourDepth = 4f;
    /// <summary>How steeply scour grows with depth: as (depth / ScourDepth) to this power. Over 1, the deepest line of a
    /// sheet of water wears faster than its edges, so the water gathers there and cuts a channel.</summary>
    public float ScourPower = 1f;

    private float Scoured(int n, float h, float floor, float dt)
    {
        if (Scour <= 0f) return floor;
        var coarse = _coarse;
        float worn = Math.Max(0f, _coarse.StartFloor[n] - floor);
        float share = Math.Min(h, ScourDepth) / ScourDepth;
        float wear = Scour * ScourDepth * (ScourPower == 1f ? share : ExactMath.Pow(share, ScourPower)) * dt / (1 + worn / Hardening);
        return Math.Max(Math.Min(floor, coarse.Base[n] + MinThickness), floor - wear);
    }

    private void Erode(float dt)
    {
        var coarse = _coarse;
        // Strongest at the start, easing down (see Ease).
        float ease = Ease == 1f ? 1f : ExactMath.Pow(Ease, 1f - 2f * (float)Math.Clamp((Time - _erodeStart) / _erodeLength, 0, 1));
        float speedup = Speedup * ease;
        Sediment ??= new float[coarse.Nodes];
        _carried ??= new float[coarse.Nodes]; _sedimentRatio ??= new float[coarse.Nodes]; _nextSediment ??= new float[coarse.Nodes];
        var sediment = Sediment; var carried = _carried; var ratio = _sedimentRatio; var next = _nextSediment;
        _slid ??= new float[coarse.Nodes];
        var startFloor = coarse.StartFloor; var slid = _slid;

        // What share of its sediment each node's water sends out this step (all of it at most).
        ForBlocks(coarse.Nodes, (first, end) =>
        {
            for (int n = first; n < end; n++)
            {
                float volume = Depth[n] * _area;
                carried[n] = volume > 1e-3f ? sediment[n] / volume : 0f;
                float outs = 0;
                for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
                {
                    float q = Flow[k];
                    if (q > 0) outs += q;
                }
                float sends = outs * dt * carried[n];
                ratio[n] = sends > sediment[n] ? sediment[n] / sends : 1f;
            }
        });

        // Sediment through the faces, then each node's exchange with its floor (the new floors kept apart, in slid,
        // until all are worked out; the slides then work from them).
        ForBlocks(coarse.Nodes, (first, end) =>
        {
            for (int n = first; n < end; n++)
            {
                float s = sediment[n], through = 0, slope = 0;
                float level = coarse.Floor[n] + Depth[n];
                for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
                {
                    float q = Flow[k];
                    int m = _other[k];
                    if (q > 0) s -= q * dt * carried[n] * ratio[n];
                    else if (q < 0 && m >= 0) s -= q * dt * carried[m] * ratio[m];
                    float abs = Math.Abs(q);
                    through += abs;
                    float drop = m >= 0 ? Math.Abs(level - coarse.Floor[m] - Depth[m]) : 0f;
                    slope += abs * Math.Min(SteepestCut, drop / coarse.LinkLength[k]);
                }
                s = Math.Max(0f, s);
                float h = Depth[n];
                if (h < 0.02f || through <= 0)
                {
                    // Dry or still: drops everything.
                    slid[n] = Math.Min(coarse.Ceiling[n] - 1f, coarse.Floor[n] + speedup * s / _area);
                    next[n] = 0;
                    if (h >= 0.02f) slid[n] = Scoured(n, h, slid[n], dt * ease);
                    continue;
                }
                float speed = through / (2 * _width * h); // through a column's width each way
                float surface = slope / through; // the water's own slope, for incision
                slope = Math.Max(FlattestCut, surface);
                float can = Carry * speed * slope * h * _area;
                if (can > s)
                {
                    float worn = Math.Max(0f, startFloor[n] - coarse.Floor[n]);
                    float take = Pick * (can - s) * dt / (1 + worn / Hardening);
                    float most = Math.Max(0f, coarse.Floor[n] - coarse.Base[n] - MinThickness) * _area / speedup;
                    take = Math.Min(take, Math.Min(most, MostWear * ease * dt * _area / speedup)); // no more than MostWear blocks a second
                    s += take;
                    slid[n] = coarse.Floor[n] - speedup * take / _area;
                }
                else
                {
                    float put = Drop * (s - can) * dt;
                    s -= put;
                    slid[n] = Math.Min(coarse.Ceiling[n] - 1f, coarse.Floor[n] + speedup * put / _area);
                }
                next[n] = s;
                slid[n] = Scoured(n, h, slid[n], dt * ease);
            }
        });
        (Sediment, _nextSediment) = (next, sediment);

        // Slides: each node gives ground to lower neighbours it stands too steeply over, and takes from higher ones.
        float slideStep = Math.Min(0.25f, SlideRate * speedup * dt), creep = Creep * speedup * dt;
        ForBlocks(coarse.Nodes, (first, end) =>
        {
            for (int n = first; n < end; n++)
            {
                float change = 0;
                for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
                {
                    int m = _other[k];
                    if (m < 0) continue;
                    float d = slid[n] - slid[m], ad = Math.Abs(d), length = coarse.LinkLength[k];
                    float started = Math.Abs(startFloor[n] - startFloor[m]);
                    if (started <= Slide * length) change -= Math.Min(0.05f, creep / (length * length)) * d;
                    float stood = Math.Max(Slide * length, started);
                    if (ad <= stood) continue;
                    float move = slideStep * (ad - stood) / 2;
                    change += d > 0 ? -move : move;
                }
                float lowest = coarse.Base[n] + MinThickness;
                coarse.Floor[n] = Math.Max(lowest, Math.Min(coarse.Ceiling[n] - 1f, slid[n] + change));
            }
        });
    }
}
