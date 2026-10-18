namespace TransportMadness.Sim;

/// <summary>xoshiro256** seeded through SplitMix64. Own implementation so results never depend on the runtime's System.Random.</summary>
public sealed class SimRandom
{
    ulong _s0, _s1, _s2, _s3;

    public SimRandom(ulong seed)
    {
        _s0 = SplitMix(ref seed);
        _s1 = SplitMix(ref seed);
        _s2 = SplitMix(ref seed);
        _s3 = SplitMix(ref seed);
    }

    public ulong NextUInt64()
    {
        ulong result = Rotl(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, maxExclusive).</summary>
    public int NextInt(int maxExclusive) => (int)((NextUInt64() >> 33) % (ulong)maxExclusive);

    /// <summary>Uniform in [0, 1).</summary>
    public float NextFloat() => (NextUInt64() >> 40) * (1f / (1UL << 24));

    public (ulong, ulong, ulong, ulong) State => (_s0, _s1, _s2, _s3);

    static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
