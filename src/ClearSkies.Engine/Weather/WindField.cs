using System.Numerics;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Weather;

/// <summary>Wind's tunable values (see <see cref="WindField"/>). Fields, so the debug panel's sliders can edit them in place.
/// Speeds are m/s, lengths metres, times seconds.</summary>
public sealed class WindSettings
{
    /// <summary>Wind speed in the core of a base current: exceeded 10% of the time. Half of it is typical, and between
    /// currents it is near zero.</summary>
    public float BaseSpeed = 4f;
    /// <summary>The base currents' scale: their direction holds for about half of it.</summary>
    public float BaseWavelength = 1500f;
    /// <summary>How sharply base flow gathers into streams with calm between: the potential is tanh(this × noise).</summary>
    public float StreamSharpness = 2.5f;

    /// <summary>Eddies' speed (exceeded 10% of the time), on top of the base currents.</summary>
    public float EddySpeed = 1f;
    public float EddyWavelength = 450f;
    /// <summary>How fast eddies roll across the world, downwind along a direction the seed picks.</summary>
    public float EddyDrift = 2f;

    /// <summary>How much a gust adds to the local wind (0.8: up to 1.8 times), and a lull takes away (0.5: down to half).</summary>
    public float GustUp = 0.8f, GustDown = 0.5f;
    /// <summary>Gust envelope noise between these is a gust (or, negated, a lull) building; past the upper, a full one.</summary>
    public float GustStart = 0.3f, GustFull = 0.8f;
    public float GustSize = 320f;
    /// <summary>The gust envelope's time scale: a gust builds over about a sixth of it.</summary>
    public float GustPeriod = 30f;

    /// <summary>Calm mask noise below <see cref="CalmFull"/> is dead calm, rising to full wind at <see cref="CalmEdge"/>.</summary>
    public float CalmFull = -0.6f, CalmEdge = -0.2f;
    public float CalmSize = 2000f;
    /// <summary>How long dead zones take to open, close and wander.</summary>
    public float CalmPeriod = 600f;

    /// <summary>Wind dies down within this many chunks of terrain, to zero at it.</summary>
    public float TerrainReach = 3f;
    /// <summary>A chunk counts as terrain when at least this fraction of it is solid.</summary>
    public float TerrainFraction = 1f / 8f;

    /// <summary>Scales vertical wind after the curl, to keep lift moderate (1: as much up and down as sideways).</summary>
    public float VerticalScale = 0.3f;
}

/// <summary>
/// The world's wind: a pure function of the world seed, position and the shared tick clock, so every machine computes the
/// same wind with nothing sent over the network (only the seed, and the clock, which clock sync already lines up).
///
/// Wind is the curl of a vector potential P evaluated at each chunk's centre, w = ∇ × P, so it is divergence-free: air is
/// never created or destroyed, ships don't pile up in traps, and where air converges sideways it has to rise or sink, giving
/// steady updrafts and downdrafts. P is shaped, and the curl does the rest:
/// <code>P = terrain · calm · gust · (base + eddies)</code>
/// <list type="bullet">
/// <item>Base currents: large static noise, sharpened (tanh) so flow gathers into streams with calm between. The routes
/// players learn.</item>
/// <item>Eddies: smaller noise rolling across the world over time; bumps along a route.</item>
/// <item>Gusts and lulls: an envelope over space and time that swells and eases the local wind over seconds.</item>
/// <item>Dead zones: a slow calm mask that opens, closes and wanders over minutes.</item>
/// <item>Terrain: s², s = smoothstep(distance to terrain / reach), so wind dies down near terrain and is zero at it. Read from
/// the loaded chunks, player edits included; an unloaded chunk counts as empty (the authority over a ship has the terrain
/// around it loaded, and forces are worked out again next tick).</item>
/// </list>
/// Any scalar times P keeps the curl exactly divergence-free; only <see cref="WindSettings.VerticalScale"/> bends that, a
/// little. Wind is derived per chunk by central differences of the neighbours' P, then sampled anywhere by trilinear
/// interpolation between chunk centres, so it never steps at chunk borders.
///
/// Main thread only. Time-varying values are cached for the current tick; terrain distances for
/// <see cref="TerrainRefreshTicks"/>, so an edit reaches the wind within half a second.
/// </summary>
public sealed class WindField
{
    private const int S = ChunkData.Size;

    // Measured over 20,000 random points and times (horizontal speed, each layer alone, default sharpness): at
    // amplitude = speed · λ / 2π, base currents exceed 1.50 × the speed 10% of the time and eddies 0.80 ×. These bring
    // both to 1 ×, so BaseSpeed and EddySpeed are the speeds exceeded 10% of the time.
    private const float BaseCalibration = 1f / 1.50f, EddyCalibration = 1f / 0.80f;

    /// <summary>How long (ticks) a chunk's terrain distance is kept before it's looked at again.</summary>
    public const int TerrainRefreshTicks = 30;

    // Base potential (static) and eddies (drifting), one noise per component; then the gust and calm envelopes.
    private readonly FastNoiseLite[] _base = new FastNoiseLite[3];
    private readonly FastNoiseLite[] _eddy = new FastNoiseLite[3];
    private readonly FastNoiseLite _gust, _calm;
    private readonly Vector2 _eddyDirection;

    private readonly ChunkVolume? _terrain;
    private readonly Func<uint> _tick;
    private readonly double _tickSeconds;

    // Per tick: each chunk's potential and wind. Kept: terrain distance (chunks) with the tick it was found.
    private uint _cachedTick = uint.MaxValue;
    private readonly Dictionary<ChunkPosition, Vector3> _potential = new();
    private readonly Dictionary<ChunkPosition, Vector3> _wind = new();
    private readonly Dictionary<ChunkPosition, (float Distance, uint Tick)> _terrainDistance = new();
    private const int MaxKeptTerrain = 32768;

    /// <param name="seed">The world's seed.</param>
    /// <param name="tick">The shared tick clock: wind drifts and gusts with it, so it must be the one every machine agrees on.</param>
    /// <param name="terrain">The static world's chunks, for the terrain mask; null for none (open sky everywhere).</param>
    public WindField(ulong seed, Func<uint> tick, double tickSeconds, ChunkVolume? terrain)
    {
        _tick = tick;
        _tickSeconds = tickSeconds;
        _terrain = terrain;
        for (int i = 0; i < 3; i++)
        {
            _base[i] = Noise(seed, 101 + i);
            _eddy[i] = Noise(seed, 201 + i);
        }
        _gust = Noise(seed, 301);
        _calm = Noise(seed, 401);
        float angle = (float)(Mix(seed, 501) % 3600 / 3600.0 * 2 * System.Math.PI);
        _eddyDirection = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
    }

    public WindSettings Settings { get; } = new();

    /// <summary>When set, wind is this everywhere instead (a debug override, on this machine only).</summary>
    public Vector3? Override { get; set; }

    /// <summary>Seconds of shared game time: the tick clock's.</summary>
    public double Time => _tick() * _tickSeconds;

    /// <summary>The wind (m/s) at a world position, interpolated between the 8 chunk centres around it.</summary>
    public Vector3 Sample(Vector3 position)
    {
        if (Override is { } o) return o;
        Refresh();
        // Chunk centres sit at (c + 0.5)·S; find the cell of centres around the point.
        var g = position / S - new Vector3(0.5f);
        int x0 = (int)MathF.Floor(g.X), y0 = (int)MathF.Floor(g.Y), z0 = (int)MathF.Floor(g.Z);
        float fx = g.X - x0, fy = g.Y - y0, fz = g.Z - z0;
        Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;
        var c00 = Lerp(ChunkWind(new(x0, y0,     z0)),     ChunkWind(new(x0 + 1, y0,     z0)),     fx);
        var c10 = Lerp(ChunkWind(new(x0, y0 + 1, z0)),     ChunkWind(new(x0 + 1, y0 + 1, z0)),     fx);
        var c01 = Lerp(ChunkWind(new(x0, y0,     z0 + 1)), ChunkWind(new(x0 + 1, y0,     z0 + 1)), fx);
        var c11 = Lerp(ChunkWind(new(x0, y0 + 1, z0 + 1)), ChunkWind(new(x0 + 1, y0 + 1, z0 + 1)), fx);
        return Lerp(Lerp(c00, c10, fy), Lerp(c01, c11, fy), fz);
    }

    /// <summary>The wind at a chunk's centre (no override): the curl of the neighbours' potential.</summary>
    public Vector3 ChunkWind(ChunkPosition c)
    {
        Refresh();
        if (_wind.TryGetValue(c, out var w)) return w;
        // Central differences over the 6 neighbours, two chunks apart.
        var px0 = Potential(c.Offset(-1, 0, 0)); var px1 = Potential(c.Offset(1, 0, 0));
        var py0 = Potential(c.Offset(0, -1, 0)); var py1 = Potential(c.Offset(0, 1, 0));
        var pz0 = Potential(c.Offset(0, 0, -1)); var pz1 = Potential(c.Offset(0, 0, 1));
        const float inv = 1f / (2 * S);
        w = new Vector3(
            ((py1.Z - py0.Z) - (pz1.Y - pz0.Y)) * inv,
            ((pz1.X - pz0.X) - (px1.Z - px0.Z)) * inv * Settings.VerticalScale,
            ((px1.Y - px0.Y) - (py1.X - py0.X)) * inv);
        _wind[c] = w;
        return w;
    }

    /// <summary>What makes up the wind at a chunk, for the debug panel.</summary>
    public readonly record struct Parts(float Gust, float Calm, float Terrain, float TerrainDistance);

    public Parts PartsAt(ChunkPosition c)
    {
        var centre = Centre(c);
        double t = Time;
        return new Parts(GustFactor(centre, t), CalmFactor(centre, t), TerrainFactor(c), TerrainDistance(c));
    }

    /// <summary>Forgets every cached value (after changing a setting, say).</summary>
    public void Invalidate()
    {
        _potential.Clear();
        _wind.Clear();
        _terrainDistance.Clear();
        _cachedTick = uint.MaxValue;
    }

    private void Refresh()
    {
        uint tick = _tick();
        if (tick == _cachedTick) return;
        _cachedTick = tick;
        _potential.Clear();
        _wind.Clear();
        if (_terrainDistance.Count > MaxKeptTerrain) _terrainDistance.Clear();
    }

    private static Vector3 Centre(ChunkPosition c) => new((c.X + 0.5f) * S, (c.Y + 0.5f) * S, (c.Z + 0.5f) * S);

    private Vector3 Potential(ChunkPosition c)
    {
        if (_potential.TryGetValue(c, out var p)) return p;
        var s = Settings;
        var x = Centre(c);
        double t = Time;

        float mask = TerrainFactor(c) * CalmFactor(x, t);
        if (mask > 0f)
        {
            // Speed from an octave scales as amplitude / wavelength: amplitude = speed · λ / 2π, times a factor measured so
            // the setting is the speed exceeded 10% of the time (see the calibration constants).
            float baseAmp = s.BaseSpeed * BaseCalibration * s.BaseWavelength / (2 * MathF.PI);
            var bq = x / s.BaseWavelength;
            var basePart = new Vector3(Stream(_base[0], bq), Stream(_base[1], bq), Stream(_base[2], bq)) * baseAmp;

            float eddyAmp = s.EddySpeed * EddyCalibration * s.EddyWavelength / (2 * MathF.PI);
            double drift = s.EddyDrift * t;
            var eq = new Vector3((float)((x.X - _eddyDirection.X * drift) / s.EddyWavelength), x.Y / s.EddyWavelength,
                                 (float)((x.Z - _eddyDirection.Y * drift) / s.EddyWavelength));
            var eddyPart = new Vector3(_eddy[0].GetNoise(eq.X, eq.Y, eq.Z), _eddy[1].GetNoise(eq.X, eq.Y, eq.Z),
                                       _eddy[2].GetNoise(eq.X, eq.Y, eq.Z)) * eddyAmp;

            p = (basePart + eddyPart) * (mask * GustFactor(x, t));
        }
        else p = Vector3.Zero;
        _potential[c] = p;
        return p;
    }

    private float Stream(FastNoiseLite noise, Vector3 q) => MathF.Tanh(Settings.StreamSharpness * noise.GetNoise(q.X, q.Y, q.Z));

    /// <summary>1 when steady, up to 1 + GustUp in a gust, down to 1 − GustDown in a lull.</summary>
    private float GustFactor(Vector3 x, double t)
    {
        var s = Settings;
        float n = _gust.GetNoise(x.X / s.GustSize, x.Z / s.GustSize, (float)(t / s.GustPeriod));
        return 1f + s.GustUp * SmoothStep(s.GustStart, s.GustFull, n) - s.GustDown * SmoothStep(s.GustStart, s.GustFull, -n);
    }

    /// <summary>0 in a dead zone, 1 outside one.</summary>
    private float CalmFactor(Vector3 x, double t)
    {
        var s = Settings;
        float n = _calm.GetNoise(x.X / s.CalmSize, x.Z / s.CalmSize, (float)(t / s.CalmPeriod));
        return SmoothStep(s.CalmFull, s.CalmEdge, n);
    }

    /// <summary>0 at terrain, 1 at <see cref="WindSettings.TerrainReach"/> chunks or further; squared so its slope is zero
    /// at terrain too.</summary>
    private float TerrainFactor(ChunkPosition c)
    {
        float s = SmoothStep(0f, 1f, TerrainDistance(c) / MathF.Max(Settings.TerrainReach, 1e-3f));
        return s * s;
    }

    /// <summary>Distance (chunks, centre to centre) to the nearest terrain chunk within reach, or the reach if none.</summary>
    private float TerrainDistance(ChunkPosition c)
    {
        uint tick = _tick();
        if (_terrainDistance.TryGetValue(c, out var known) && tick - known.Tick < TerrainRefreshTicks) return known.Distance;

        float reach = Settings.TerrainReach;
        float best = reach;
        if (_terrain is not null)
        {
            int r = (int)MathF.Ceiling(reach);
            int threshold = (int)MathF.Ceiling(Settings.TerrainFraction * ChunkData.Volume);
            for (int dz = -r; dz <= r; dz++)
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d >= best) continue;
                if (_terrain.GetData(c.Offset(dx, dy, dz)) is { } data && data.CollidingCount >= threshold) best = d;
            }
        }
        _terrainDistance[c] = (best, tick);
        return best;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = System.Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static FastNoiseLite Noise(ulong seed, int salt)
    {
        var n = new FastNoiseLite(unchecked((int)Mix(seed, salt)));
        n.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        n.SetFrequency(1f); // coordinates come in already divided by their wavelength
        return n;
    }

    // SplitMix64 of the seed and a salt: independent noise per use from one world seed.
    private static ulong Mix(ulong seed, int salt)
    {
        ulong z = seed + 0x9E3779B97F4A7C15UL * (ulong)(salt + 1);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
