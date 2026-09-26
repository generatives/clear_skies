namespace ClearSkies.Game.Generation;

/// <summary>Minimal SplitMix64 PRNG for deterministic discrete draws (counts, positions, sizes).</summary>
public struct SplitMix64Rng
{
    private ulong _state;

    public SplitMix64Rng(ulong seed) => _state = seed;

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// Uniform float in [0, 1) with 24 bits of precision.
    public float NextFloat01() => (NextUInt64() >> 40) * (1f / (1 << 24));

    public float NextRange(float min, float max) => min + (max - min) * NextFloat01();
}
