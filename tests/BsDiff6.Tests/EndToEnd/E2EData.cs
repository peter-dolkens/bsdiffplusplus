namespace BsDiff6.Tests.EndToEnd;

/// <summary>Synthetic old/new pairs for end-to-end tests.</summary>
internal static class E2EData
{
    public static byte[] Random(Random rng, int n)
    {
        byte[] b = new byte[n];
        rng.NextBytes(b);
        return b;
    }

    /// <summary>Executable-like data: a code-ish region of repeated opcodes with embedded little-endian addresses, plus a string table.</summary>
    public static byte[] Structured(Random rng, int n)
    {
        var o = new List<byte>(n);
        string[] words = { "error", "file", "open", "read", "write", "close", "buffer", "length", "index", "value", "\0" };
        while (o.Count < n)
        {
            int kind = rng.Next(3);
            if (kind == 0)
            {
                int addr = rng.Next(0x1000, 0x80000);
                o.Add(0xE8); o.Add((byte)addr); o.Add((byte)(addr >> 8)); o.Add((byte)(addr >> 16)); o.Add(0);
            }
            else if (kind == 1)
            {
                o.Add((byte)(0x40 + rng.Next(8))); o.Add(0x89); o.Add((byte)rng.Next(256));
            }
            else
            {
                foreach (char c in words[rng.Next(words.Length)]) o.Add((byte)c);
            }
        }
        return o.GetRange(0, n).ToArray();
    }

    /// <summary>Planted insertions and deletions.</summary>
    public static byte[] PlantEdits(Random rng, byte[] old, int edits)
    {
        var o = new List<byte>(old);
        for (int e = 0; e < edits && o.Count > 0; e++)
        {
            int at = rng.Next(o.Count);
            if (rng.Next(2) == 0)
            {
                int len = rng.Next(1, 200);
                o.InsertRange(at, Random(rng, len));
            }
            else
            {
                int len = Math.Min(rng.Next(1, 200), o.Count - at);
                o.RemoveRange(at, len);
            }
        }
        return o.ToArray();
    }

    /// <summary>The old file cut into chunks that are permuted (shifted regions).</summary>
    public static byte[] Shuffle(Random rng, byte[] old, int chunks)
    {
        var pieces = new List<byte[]>();
        int pos = 0;
        for (int c = 0; c < chunks; c++)
        {
            int len = c == chunks - 1 ? old.Length - pos : Math.Min(old.Length - pos, rng.Next(1, 2 * old.Length / chunks + 1));
            pieces.Add(old.AsSpan(pos, len).ToArray());
            pos += len;
        }
        return pieces.OrderBy(_ => rng.Next()).SelectMany(p => p).ToArray();
    }

    /// <summary>Each byte replaced by a random one with probability <paramref name="p"/>.</summary>
    public static byte[] Noise(Random rng, byte[] old, double p)
    {
        byte[] b = (byte[])old.Clone();
        for (int i = 0; i < b.Length; i++) if (rng.NextDouble() < p) b[i] = (byte)rng.Next(256);
        return b;
    }

    /// <summary>Executable-style change: every embedded address (after an 0xE8) shifted by a constant.</summary>
    public static byte[] Relocate(byte[] old, int delta)
    {
        byte[] b = (byte[])old.Clone();
        for (int i = 0; i + 4 < b.Length; i++)
        {
            if (b[i] != 0xE8) continue;
            int v = b[i + 1] | (b[i + 2] << 8) | (b[i + 3] << 16);
            v += delta;
            b[i + 1] = (byte)v; b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)(v >> 16);
            i += 4;
        }
        return b;
    }

    /// <summary>Finds bench/corpus by walking up from the test binary; null when absent.</summary>
    public static string? CorpusDir
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                string c = Path.Combine(d.FullName, "bench", "corpus");
                if (Directory.Exists(c)) return c;
                d = d.Parent;
            }
            return null;
        }
    }
}

/// <summary>A theory that is skipped when bench/corpus does not exist.</summary>
public sealed class CorpusTheoryAttribute : TheoryAttribute
{
    public CorpusTheoryAttribute()
    {
        if (E2EData.CorpusDir == null) Skip = "bench/corpus not found";
    }
}
