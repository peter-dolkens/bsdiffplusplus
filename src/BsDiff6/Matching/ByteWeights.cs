namespace BsDiff6.Matching;

/// <summary>Per-byte weights w(c) for the maps φ(c) = ±w(c) (thesis §1.6 note 4).</summary>
public static class ByteWeights
{
    /// <summary>All weights 1.</summary>
    public static float[] Uniform()
    {
        var w = new float[256];
        w.AsSpan().Fill(1f);
        return w;
    }

    /// <summary>
    /// w(c) = 1 / sqrt(freq(c)) with freq counted over <paramref name="a"/> ‖ <paramref name="b"/>,
    /// normalised so that the mean of w² over the data is 1. Bytes that do not occur get weight 0.
    /// </summary>
    /// <param name="frequencyFloor">
    /// Frequencies are floored at <c>frequencyFloor / 256</c> before taking 1/sqrt, so that rare
    /// bytes are not weighted above a byte of uniform frequency (when 1). 0 = no floor.
    /// </param>
    public static float[] InverseSqrtFrequency(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, double frequencyFloor = 0)
    {
        var counts = new long[256];
        Count(a, counts);
        Count(b, counts);
        return FromCounts(counts, frequencyFloor);
    }

    public static float[] FromCounts(ReadOnlySpan<long> counts, double frequencyFloor = 0)
    {
        if (counts.Length != 256) throw new ArgumentException("need 256 counts");
        long total = 0;
        int distinct = 0;
        for (int c = 0; c < 256; c++)
        {
            if (counts[c] < 0) throw new ArgumentException("negative count");
            total += counts[c];
            if (counts[c] > 0) distinct++;
        }
        var w = new float[256];
        if (total == 0) { w.AsSpan().Fill(1f); return w; }
        double floor = frequencyFloor * total / 256.0;
        var raw = new double[256];
        double meanSq = 0;
        for (int c = 0; c < 256; c++)
        {
            if (counts[c] == 0) continue;
            raw[c] = 1 / Math.Sqrt(Math.Max(counts[c], floor));
            meanSq += counts[c] * raw[c] * raw[c];
        }
        // Normalise: Σ_c count(c) w(c)² / total = 1.
        double scale = Math.Sqrt(total / meanSq);
        for (int c = 0; c < 256; c++) w[c] = (float)(raw[c] * scale);
        return w;
    }

    private static void Count(ReadOnlySpan<byte> data, long[] counts)
    {
        // Four interleaved tables avoid store-to-load stalls on runs of equal bytes.
        Span<int> c0 = stackalloc int[256], c1 = stackalloc int[256], c2 = stackalloc int[256], c3 = stackalloc int[256];
        int pos = 0;
        while (pos < data.Length)
        {
            int chunk = Math.Min(data.Length - pos, 1 << 28);
            c0.Clear(); c1.Clear(); c2.Clear(); c3.Clear();
            ReadOnlySpan<byte> d = data.Slice(pos, chunk);
            int i = 0;
            for (; i + 4 <= d.Length; i += 4)
            {
                c0[d[i]]++; c1[d[i + 1]]++; c2[d[i + 2]]++; c3[d[i + 3]]++;
            }
            for (; i < d.Length; i++) c0[d[i]]++;
            for (int c = 0; c < 256; c++) counts[c] += (long)c0[c] + c1[c] + c2[c] + c3[c];
            pos += chunk;
        }
    }
}
