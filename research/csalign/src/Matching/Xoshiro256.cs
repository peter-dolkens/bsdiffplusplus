namespace BsDiff6.Matching;

/// <summary>
/// xoshiro256** (Blackman and Vigna), seeded through SplitMix64. Self-contained so that output is
/// identical across runs, runtimes and platforms (unlike <see cref="System.Random"/>).
/// </summary>
public sealed class Xoshiro256
{
    private ulong _s0, _s1, _s2, _s3;

    public Xoshiro256(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix64(ref x);
        _s1 = SplitMix64(ref x);
        _s2 = SplitMix64(ref x);
        _s3 = SplitMix64(ref x);
    }

    private static ulong SplitMix64(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

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

    /// <summary>Uniform integer in [0, bound), bound &gt; 0, without modulo bias.</summary>
    public ulong NextBelow(ulong bound)
    {
        if (bound == 0) throw new ArgumentOutOfRangeException(nameof(bound));
        ulong threshold = (0UL - bound) % bound; // 2^64 mod bound
        while (true)
        {
            ulong r = NextUInt64();
            if (r >= threshold) return r % bound;
        }
    }

    public int NextInt(int bound) => (int)NextBelow((ulong)bound);

    public bool NextBool() => (NextUInt64() >> 63) != 0;

    /// <summary>Uniform double in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));
}
