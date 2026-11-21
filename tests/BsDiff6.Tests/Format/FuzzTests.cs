using System.Diagnostics;
using BsDiff6.Format;

namespace BsDiff6.Tests.Format;

/// <summary>
/// Mutation fuzzing of <see cref="PatchReader.Apply"/>: the only acceptable outcomes are a
/// <see cref="PatchFormatException"/> or a successful result. Default runs are short; the long run
/// is behind <c>[Trait("Category", "LongFuzz")]</c>
/// (<c>dotnet test ... --filter Category=LongFuzz</c>, or exclude with <c>Category!=LongFuzz</c>).
/// </summary>
public class FuzzTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public FuzzTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

    /// <summary>Outcome histogram of the last run on this thread (exception message → count), for coverage checks.</summary>
    [ThreadStatic] private static Dictionary<string, int>? _outcomes;

    private sealed class Corpus
    {
        public readonly List<(byte[] old, byte[] patch)> Items = new List<(byte[] old, byte[] patch)>();
    }

    private static Corpus BuildCorpus()
    {
        var c = new Corpus();
        var rng = new Random(2024);
        var raw = new EncodeOptions { ControlCodec = Codec.None, NonZeroCodec = Codec.None, ExtraCodec = Codec.None };
        for (int i = 0; i < 16; i++)
        {
            int n = i == 0 ? 0 : rng.Next(1, 600);
            int m = i == 1 ? 0 : rng.Next(1, 700);
            byte[] old = i % 2 == 0 ? FormatTestData.Structured(rng, n) : FormatTestData.RandomBytes(rng, n);
            var (@new, align) = FormatTestData.Derive(rng, old, m, 0.85, 0.1, 120);
            EncodeOptions opts = i % 3 == 2 ? new EncodeOptions() : new EncodeOptions
            {
                ControlCodec = raw.ControlCodec, NonZeroCodec = raw.NonZeroCodec, ExtraCodec = raw.ExtraCodec,
                DiffMode = (DiffMode)(i % 4), MapModel = (MapModel)(i % MidpointCoder.ModelCount),
            };
            c.Items.Add((old, PatchWriter.Encode(old, @new, align, opts)));
        }
        return c;
    }

    private static readonly Lazy<Corpus> Shared = new Lazy<Corpus>(BuildCorpus);

    /// <summary>Returns true if Apply succeeded. Any exception other than PatchFormatException fails.</summary>
    private static bool TryApply(byte[] old, byte[] patch, string what)
    {
        try
        {
            PatchReader.Apply(old, patch);
            if (_outcomes != null) { _outcomes.TryGetValue("(success)", out int c); _outcomes["(success)"] = c + 1; }
            return true;
        }
        catch (PatchFormatException e)
        {
            if (_outcomes != null) { _outcomes.TryGetValue(e.Message, out int c); _outcomes[e.Message] = c + 1; }
            return false;
        }
        catch (Exception e)
        {
            throw new Xunit.Sdk.XunitException($"{what}: unexpected {e.GetType().Name}: {e.Message}\npatch={Convert.ToHexString(patch)}\n{e}");
        }
    }

    private void WithTimeLimit(TimeSpan limit, Action body)
    {
        var outcomes = new Dictionary<string, int>();
        var task = Task.Run(() =>
        {
            _outcomes = outcomes;
            try { body(); } finally { _outcomes = null; }
        });
        if (!task.Wait(limit)) throw new Xunit.Sdk.XunitException($"fuzz run exceeded {limit} (hang?)");
        task.GetAwaiter().GetResult();
        foreach (var kv in outcomes.OrderByDescending(kv => kv.Value)) _out.WriteLine($"{kv.Value,8}  {kv.Key}");
    }

    [Fact]
    public void EveryTruncationOfSmallPatches()
    {
        WithTimeLimit(TimeSpan.FromSeconds(60), () =>
        {
            foreach (var (old, patch) in Shared.Value.Items.Take(8))
                for (int len = 0; len < patch.Length; len++)
                    Assert.False(TryApply(old, patch.AsSpan(0, len).ToArray(), $"truncate {len}"));
        });
    }

    [Fact]
    public void RandomMutations() => RunMutations(seed: 1, iterations: 20000, TimeSpan.FromSeconds(60));

    [Fact]
    [Trait("Category", "LongFuzz")]
    public void RandomMutationsLong() => RunMutations(seed: 99, iterations: 1_000_000, TimeSpan.FromMinutes(30));

    [Fact]
    public void StructuredMutations() => RunStructured(seed: 2, iterations: 20000, TimeSpan.FromSeconds(60));

    [Fact]
    [Trait("Category", "LongFuzz")]
    public void StructuredMutationsLong() => RunStructured(seed: 98, iterations: 1_000_000, TimeSpan.FromMinutes(30));

    private void RunMutations(int seed, int iterations, TimeSpan limit)
    {
        Corpus corpus = Shared.Value;
        WithTimeLimit(limit, () =>
        {
            var rng = new Random(seed);
            var slowest = Stopwatch.StartNew();
            for (int it = 0; it < iterations; it++)
            {
                var (old, basePatch) = corpus.Items[rng.Next(corpus.Items.Count)];
                byte[] p = Mutate(rng, basePatch, corpus);
                // Also sometimes feed a different old file.
                byte[] o = rng.Next(50) == 0 ? corpus.Items[rng.Next(corpus.Items.Count)].old : old;
                var sw = Stopwatch.StartNew();
                TryApply(o, p, $"iteration {it}");
                if (sw.ElapsedMilliseconds > 2000) throw new Xunit.Sdk.XunitException($"iteration {it} took {sw.ElapsedMilliseconds} ms");
            }
        });
    }

    private static byte[] Mutate(Random rng, byte[] patch, Corpus corpus)
    {
        var p = new List<byte>(patch);
        int ops = rng.Next(1, 4);
        for (int k = 0; k < ops; k++)
        {
            switch (rng.Next(6))
            {
                case 0: // bit flip
                    if (p.Count > 0) { int i = rng.Next(p.Count); p[i] ^= (byte)(1 << rng.Next(8)); }
                    break;
                case 1: // byte overwrite, biased to interesting values
                    if (p.Count > 0)
                    {
                        int i = rng.Next(p.Count);
                        byte[] interesting = { 0x00, 0x80, 0xFF, 0x7F, 0x01, 0x81 };
                        p[i] = rng.Next(2) == 0 ? interesting[rng.Next(interesting.Length)] : (byte)rng.Next(256);
                    }
                    break;
                case 2: // splice a chunk of another patch
                {
                    byte[] other = corpus.Items[rng.Next(corpus.Items.Count)].patch;
                    if (other.Length == 0 || p.Count == 0) break;
                    int len = rng.Next(1, Math.Min(64, other.Length) + 1);
                    int from = rng.Next(other.Length - len + 1);
                    int to = rng.Next(p.Count);
                    for (int i = 0; i < len && to + i < p.Count; i++) p[to + i] = other[from + i];
                    break;
                }
                case 3: // delete a range
                    if (p.Count > 1) { int i = rng.Next(p.Count); p.RemoveRange(i, Math.Min(p.Count - i, rng.Next(1, 16))); }
                    break;
                case 4: // insert random bytes
                {
                    int i = rng.Next(p.Count + 1);
                    var ins = new byte[rng.Next(1, 16)];
                    rng.NextBytes(ins);
                    p.InsertRange(i, ins);
                    break;
                }
                default: // truncate
                    if (p.Count > 0) { int len = rng.Next(p.Count); p.RemoveRange(len, p.Count - len); }
                    break;
            }
        }
        return p.ToArray();
    }

    /// <summary>
    /// Structure-aware: mutate one section (or one header field) and reassemble with consistent
    /// section lengths, so that mutations reach the control, map and decompression logic instead
    /// of failing in the header check.
    /// </summary>
    private void RunStructured(int seed, int iterations, TimeSpan limit)
    {
        Corpus corpus = Shared.Value;
        WithTimeLimit(limit, () =>
        {
            var rng = new Random(seed);
            var parts = corpus.Items.Select(x => (x.old, parts: PatchParts.Split(x.patch))).ToList();
            int successes = 0;
            for (int it = 0; it < iterations; it++)
            {
                var (old, src) = parts[rng.Next(parts.Count)];
                var pp = new PatchParts { Info = src.Info, Control = src.Control, NonZero = src.NonZero, Map = src.Map, Extra = src.Extra };
                Func<PatchInfo, PatchInfo>? edit = null;
                switch (rng.Next(5))
                {
                    case 0: pp.Control = MutateSection(rng, pp.Control); break;
                    case 1: pp.NonZero = MutateSection(rng, pp.NonZero); break;
                    case 2: pp.Map = MutateSection(rng, pp.Map); break;
                    case 3: pp.Extra = MutateSection(rng, pp.Extra); break;
                    default:
                    {
                        string[] keys = { "mode", "cc", "nc", "ec", "model", "old", "new", "ctrl", "nz", "ex", "primary" };
                        string key = keys[rng.Next(keys.Length)];
                        long delta = rng.Next(3) switch { 0 => rng.Next(-3, 4), 1 => rng.Next(-1000, 1000), _ => (long)rng.Next() };
                        edit = h => PatchParts.With(h, f =>
                        {
                            long v = f[key] + delta;
                            if (key is "mode" or "cc" or "nc" or "ec") v &= 3;
                            else if (key == "model") v &= 0xFF;
                            else v = Math.Max(0, Math.Min(v, (1L << 62)));
                            f[key] = v;
                        });
                        break;
                    }
                }
                byte[] patch = pp.Build(edit);
                if (TryApply(old, patch, $"structured iteration {it}")) successes++;
            }
        });
    }

    private static byte[] MutateSection(Random rng, byte[] s)
    {
        var p = new List<byte>(s);
        int ops = rng.Next(1, 3);
        for (int k = 0; k < ops; k++)
        {
            int kind = rng.Next(5);
            if (p.Count == 0) kind = 3;
            switch (kind)
            {
                case 0: { int i = rng.Next(p.Count); p[i] ^= (byte)(1 << rng.Next(8)); break; }
                case 1: { int i = rng.Next(p.Count); p[i] = (byte)rng.Next(256); break; }
                case 2: { int i = rng.Next(p.Count); p.RemoveAt(i); break; }
                case 3: { p.Insert(rng.Next(p.Count + 1), (byte)rng.Next(256)); break; }
                default: { int i = rng.Next(p.Count); int j = rng.Next(p.Count); (p[i], p[j]) = (p[j], p[i]); break; }
            }
        }
        return p.ToArray();
    }
}
