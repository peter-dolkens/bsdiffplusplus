using BsDiff6.Matching;

namespace BsDiff6.Tests.Matching;

/// <summary>Synthetic inputs for the matching tests.</summary>
internal static class TestData
{
    public static byte[] Random(int n, ulong seed)
    {
        var rng = new Xoshiro256(seed);
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)rng.NextUInt64();
        return b;
    }

    /// <summary>
    /// Executable-like bytes: runs of zeros, skewed "code" bytes, ASCII strings, little-endian
    /// tables of slowly increasing integers, and copies of earlier regions.
    /// </summary>
    public static byte[] BinaryLike(int n, ulong seed)
    {
        var rng = new Xoshiro256(seed);
        var b = new byte[n];
        int pos = 0;
        while (pos < n)
        {
            int kind = rng.NextInt(10);
            int len = Math.Min(n - pos, 16 + rng.NextInt(kind < 2 ? 512 : 2048));
            switch (kind)
            {
                case 0: case 1: // zeros
                    break;
                case 2: // ASCII text
                    for (int i = 0; i < len; i++) b[pos + i] = (byte)(rng.NextInt(8) == 0 ? ' ' : 'a' + rng.NextInt(26));
                    break;
                case 3: // table of 32-bit little-endian increasing values
                {
                    uint v = (uint)rng.NextUInt64() & 0xFFFFF;
                    for (int i = 0; i + 4 <= len; i += 4)
                    {
                        v += (uint)rng.NextInt(64);
                        b[pos + i] = (byte)v; b[pos + i + 1] = (byte)(v >> 8); b[pos + i + 2] = (byte)(v >> 16);
                    }
                    break;
                }
                case 4: // copy of an earlier region
                    if (pos > len)
                    {
                        int src = rng.NextInt(pos - len);
                        Array.Copy(b, src, b, pos, len);
                    }
                    break;
                default: // "code": skewed byte distribution with some zeros
                    for (int i = 0; i < len; i++)
                    {
                        double u = rng.NextDouble();
                        b[pos + i] = rng.NextInt(6) == 0 ? (byte)0 : (byte)(u * u * u * 256);
                    }
                    break;
            }
            pos += len;
        }
        return b;
    }

    /// <summary>Replaces exactly round(fraction · length) bytes, at random positions, by different random bytes.</summary>
    public static void Substitute(Span<byte> data, double fraction, Xoshiro256 rng)
    {
        int n = data.Length;
        int k = (int)Math.Round(fraction * n);
        var idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;
        for (int i = 0; i < k; i++)
        {
            int j = i + rng.NextInt(n - i);
            (idx[i], idx[j]) = (idx[j], idx[i]);
            int p = idx[i];
            data[p] = (byte)(data[p] + 1 + rng.NextInt(255));
        }
    }

    /// <summary>The best of the index's first <paramref name="verify"/> candidates by direct match count.</summary>
    public static (int oldPos, int matches) BestVerified(MismatchQuery q, ReadOnlySpan<byte> old, ReadOnlySpan<byte> block, int verify)
    {
        var cands = new MismatchCandidate[verify];
        int count = q.Find(block, cands);
        int best = -1, bestPos = int.MinValue;
        for (int i = 0; i < count; i++)
        {
            int m = MatchCounter.Count(old, cands[i].OldPos, block);
            if (m > best) { best = m; bestPos = cands[i].OldPos; }
        }
        return (bestPos, best);
    }
}
