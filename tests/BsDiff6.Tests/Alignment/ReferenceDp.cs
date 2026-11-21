using BsDiff6.Alignment;

namespace BsDiff6.Tests.Alignment;

/// <summary>
/// Straightforward version of the pruned-graph DP of DESIGN §6: same candidate sets, but a
/// dictionary of states per position with full back-pointers. Only for small inputs.
/// </summary>
internal static class ReferenceDp
{
    public const int U = int.MaxValue;     // unmatched

    public sealed class Result
    {
        public long Cost;
        public Segment[] Path = Array.Empty<Segment>();
        public List<int[]> Candidates = new();   // per position, the candidate set (for exhaustive search)
    }

    private static bool Valid(int pos, int o, int n) => o == U || (pos + o >= 0 && pos + o < n);

    private static int ByteCost(byte[] old, byte[] nw, int pos, int o, DpOptions opt)
        => o == U ? opt.UnmatchedCost : old[pos + o] == nw[pos] ? 0 : opt.MismatchCost;

    private static IEnumerable<int> Extra(int pos, LocalMatches? local, IReadOnlyList<Segment>? block, DpOptions opt, int m)
    {
        if (local != null)
        {
            for (int t = pos; t < Math.Min(m, pos + opt.LocalCandidates); t++)
                if (local.Length[t] >= Math.Max(1, opt.MinLocalMatchLength))
                    yield return local.OldPos[t] - t;
        }
        if (block != null)
        {
            foreach (Segment s in block)
                if (s.NewPos <= pos && pos < s.NewEnd && s.IsMatched) yield return s.Offset;
        }
    }

    public static Result Run(byte[] old, byte[] nw, LocalMatches? local, IReadOnlyList<Segment>? block, DpOptions opt)
    {
        int n = old.Length, m = nw.Length;
        var res = new Result();
        if (m == 0) return res;
        var dists = new List<Dictionary<int, long>>();
        var back = new List<Dictionary<int, int>>();   // state at pos → state at pos-1

        var d0 = new Dictionary<int, long>();
        var b0 = new Dictionary<int, int>();
        foreach (int o in Extra(0, local, block, opt, m).Append(U))
        {
            if (!Valid(0, o, n) || d0.ContainsKey(o)) continue;
            d0[o] = ByteCost(old, nw, 0, o, opt);
            b0[o] = int.MinValue;
        }
        dists.Add(d0); back.Add(b0);
        res.Candidates.Add(d0.Keys.ToArray());

        for (int pos = 1; pos < m; pos++)
        {
            var prev = dists[pos - 1];
            long best = prev.Values.Min();
            int bestState = prev.Where(kv => kv.Value == best).OrderBy(kv => kv.Key).First().Key;
            var cand = new List<int>();
            cand.AddRange(prev.Where(kv => kv.Key != U && Valid(pos, kv.Key, n))
                .OrderBy(kv => kv.Value).ThenBy(kv => kv.Key).Take(opt.BestCandidates).Select(kv => kv.Key));
            cand.Add(U);
            cand.AddRange(Extra(pos, local, block, opt, m).Where(o => Valid(pos, o, n)));
            var d = new Dictionary<int, long>();
            var bk = new Dictionary<int, int>();
            foreach (int s in cand.Distinct())
            {
                long viaSwitch = best + opt.SwitchCost;
                long dist; int from;
                if (prev.TryGetValue(s, out long stay) && stay <= viaSwitch) { dist = stay; from = s; }
                else { dist = viaSwitch; from = bestState; }
                d[s] = dist + ByteCost(old, nw, pos, s, opt);
                bk[s] = from;
            }
            dists.Add(d); back.Add(bk);
            res.Candidates.Add(d.Keys.ToArray());
        }

        long finalBest = dists[m - 1].Values.Min();
        int state = dists[m - 1].First(kv => kv.Value == finalBest).Key;
        res.Cost = finalBest;
        var states = new int[m];
        for (int pos = m - 1; pos >= 0; pos--)
        {
            states[pos] = state;
            state = back[pos][state];
        }
        var segs = new List<Segment>();
        for (int pos = 0; pos < m; pos++)
            segs.Add(new Segment(pos, 1, states[pos] == U ? -1 : pos + states[pos]));
        res.Path = AlignmentUtil.Canonicalize(segs);
        return res;
    }

    /// <summary>Minimum cost over all paths through the given candidate sets, by exhaustive search.</summary>
    public static long Exhaustive(byte[] old, byte[] nw, List<int[]> candidates, DpOptions opt)
    {
        int m = nw.Length;
        long best = long.MaxValue;
        void Rec(int pos, int prevState, long cost)
        {
            if (cost >= best) return;
            if (pos == m) { best = cost; return; }
            foreach (int s in candidates[pos])
            {
                long c = cost + ByteCost(old, nw, pos, s, opt);
                if (pos > 0 && s != prevState) c += opt.SwitchCost;
                Rec(pos + 1, s, c);
            }
        }
        Rec(0, 0, 0);
        return best;
    }
}
