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
    public float Ease = 1f;
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
    public float Carry = 0.05f, Pick = 0.02f, Drop = 0.02f, Speedup = 900f;
    /// <summary>Slopes count as no steeper than this (falls and cliffs too), so they don't cut without end.</summary>
    public float SteepestCut = 1f;
    public float MinThickness = 6f;
    /// <summary>The fastest a floor wears, in blocks a second (with Speedup).</summary>
    public float MostWear = 0.04f;
    public float Hardening = 8f, Slide = 1f, SlideRate = 0.002f, Creep = 0f;

    private float[]? _startFloor, _slid;

    public float[]? Sediment;
    private float[]? _carried, _sedimentRatio, _nextSediment;

    /// <summary>Standing water wears its bed: Scour blocks a second per block of water over it (up to ScourDepth),
    /// slowing as it's worn deeper (see Hardening). What it wears is gone, not carried: in still water it would only
    /// settle back. So hollows where water stands deepen into bowls.</summary>
    public float Scour = 0f, ScourDepth = 4f;

    private float Scoured(int n, float h, float floor, float dt)
    {
        if (Scour <= 0f) return floor;
        var land = _land;
        float worn = Math.Max(0f, _startFloor![n] - floor);
        float wear = Scour * Math.Min(h, ScourDepth) * dt / (1 + worn / Hardening);
        return Math.Max(Math.Min(floor, land.Base[n] + MinThickness), floor - wear);
    }

    private void Erode(float dt)
    {
        var land = _land;
        // Strongest at the start, easing down (see Ease).
        float ease = Ease == 1f ? 1f : MathF.Pow(Ease, 1f - 2f * (float)Math.Clamp((Time - _erodeStart) / _erodeLength, 0, 1));
        float speedup = Speedup * ease;
        Sediment ??= new float[land.Nodes];
        _carried ??= new float[land.Nodes]; _sedimentRatio ??= new float[land.Nodes]; _nextSediment ??= new float[land.Nodes];
        var sediment = Sediment; var carried = _carried; var ratio = _sedimentRatio; var next = _nextSediment;
        _startFloor ??= (float[])land.Floor.Clone();
        _slid ??= new float[land.Nodes];
        var startFloor = _startFloor; var slid = _slid;

        // What share of its sediment each node's water sends out this step (all of it at most).
        Parallel.For(0, (land.Nodes + 4095) / 4096, block =>
        {
            int end = Math.Min(land.Nodes, (block + 1) * 4096);
            for (int n = block * 4096; n < end; n++)
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
        Parallel.For(0, (land.Nodes + 4095) / 4096, block =>
        {
            int end = Math.Min(land.Nodes, (block + 1) * 4096);
            for (int n = block * 4096; n < end; n++)
            {
                float s = sediment[n], through = 0, slope = 0;
                float level = land.Floor[n] + Depth[n];
                for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
                {
                    float q = Flow[k];
                    int m = _other[k];
                    if (q > 0) s -= q * dt * carried[n] * ratio[n];
                    else if (q < 0 && m >= 0) s -= q * dt * carried[m] * ratio[m];
                    float abs = Math.Abs(q);
                    through += abs;
                    float drop = m >= 0 ? Math.Abs(level - land.Floor[m] - Depth[m]) : 0f;
                    slope += abs * Math.Min(SteepestCut, drop / land.LinkLength[k]);
                }
                s = Math.Max(0f, s);
                float h = Depth[n];
                if (h < 0.02f || through <= 0)
                {
                    // Dry or still: drops everything.
                    slid[n] = Math.Min(land.Ceiling[n] - 1f, land.Floor[n] + speedup * s / _area);
                    next[n] = 0;
                    if (h >= 0.02f) slid[n] = Scoured(n, h, slid[n], dt * ease);
                    continue;
                }
                float speed = through / (2 * _width * h); // through a column's width each way
                slope /= through;
                float can = Carry * speed * slope * h * _area;
                if (can > s)
                {
                    float worn = Math.Max(0f, startFloor[n] - land.Floor[n]);
                    float take = Pick * (can - s) * dt / (1 + worn / Hardening);
                    float most = Math.Max(0f, land.Floor[n] - land.Base[n] - MinThickness) * _area / speedup;
                    take = Math.Min(take, Math.Min(most, MostWear * ease * dt * _area / speedup)); // no more than MostWear blocks a second
                    s += take;
                    slid[n] = land.Floor[n] - speedup * take / _area;
                }
                else
                {
                    float put = Drop * (s - can) * dt;
                    s -= put;
                    slid[n] = Math.Min(land.Ceiling[n] - 1f, land.Floor[n] + speedup * put / _area);
                }
                next[n] = s;
                slid[n] = Scoured(n, h, slid[n], dt * ease);
            }
        });
        (Sediment, _nextSediment) = (next, sediment);

        // Slides: each node gives ground to lower neighbours it stands too steeply over, and takes from higher ones.
        float slideStep = Math.Min(0.25f, SlideRate * speedup * dt), creep = Creep * speedup * dt;
        Parallel.For(0, (land.Nodes + 4095) / 4096, block =>
        {
            int end = Math.Min(land.Nodes, (block + 1) * 4096);
            for (int n = block * 4096; n < end; n++)
            {
                float change = 0;
                for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
                {
                    int m = _other[k];
                    if (m < 0) continue;
                    float d = slid[n] - slid[m], ad = Math.Abs(d), length = land.LinkLength[k];
                    float started = Math.Abs(startFloor[n] - startFloor[m]);
                    if (started <= Slide * length) change -= Math.Min(0.05f, creep / (length * length)) * d;
                    float stood = Math.Max(Slide * length, started);
                    if (ad <= stood) continue;
                    float move = slideStep * (ad - stood) / 2;
                    change += d > 0 ? -move : move;
                }
                float lowest = land.Base[n] + MinThickness;
                land.Floor[n] = Math.Max(lowest, Math.Min(land.Ceiling[n] - 1f, slid[n] + change));
            }
        });
    }
}
