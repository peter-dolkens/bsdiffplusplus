using System.Runtime.CompilerServices;

namespace BsDiff6.Alignment;

/// <summary>Parameters of the pruned-graph shortest path (DESIGN §6). Defaults are the thesis's.</summary>
public sealed class DpOptions
{
    /// <summary>Offsets of the longest matches at pos, pos+1, …, pos+LocalCandidates-1 are candidates.</summary>
    public int LocalCandidates { get; set; } = 31;

    /// <summary>Number of lowest-distance offset states at pos-1 carried over as candidates.</summary>
    public int BestCandidates { get; set; } = 31;

    public int MismatchCost { get; set; } = 2;
    public int UnmatchedCost { get; set; } = 1;
    public int SwitchCost { get; set; } = 20;

    /// <summary>Longest matches shorter than this are not used as candidates.</summary>
    public int MinLocalMatchLength { get; set; } = 1;

    internal void Check()
    {
        if (LocalCandidates < 0 || BestCandidates < 0 || LocalCandidates > 1024 || BestCandidates > 1024)
            throw new ArgumentOutOfRangeException(nameof(DpOptions), "candidate counts must be in [0, 1024]");
        if (MismatchCost < 0 || UnmatchedCost < 0 || SwitchCost < 0)
            throw new ArgumentOutOfRangeException(nameof(DpOptions), "costs must be non-negative");
    }
}

/// <summary>Counters filled in by <see cref="DpAligner.Align(ReadOnlySpan{byte}, ReadOnlySpan{byte}, LocalMatches?, IReadOnlyList{Segment}?, DpOptions?, DpDiagnostics?)"/>.</summary>
public sealed class DpDiagnostics
{
    /// <summary>Total cost of the chosen path (equals <see cref="DpAligner.PathCost"/> of the result).</summary>
    public long Cost { get; internal set; }

    /// <summary>Largest number of run records alive at once.</summary>
    public int PeakLiveRecords { get; internal set; }

    /// <summary>Run records alive at the end, before traceback.</summary>
    public int FinalLiveRecords { get; internal set; }

    /// <summary>Capacity the record pool grew to.</summary>
    public int RecordPoolCapacity { get; internal set; }

    /// <summary>Largest number of states at one position.</summary>
    public int PeakStates { get; internal set; }

    /// <summary>Sum over positions of the number of states (for the average).</summary>
    public long TotalStates { get; internal set; }
}

/// <summary>Pruned-graph shortest path combining local and block alignment (thesis §2.6; DESIGN §6).</summary>
public static class DpAligner
{
    /// <summary>Offset key of the unmatched state. Real offsets lie in (-m, n), so never collide.</summary>
    private const int UnmatchedKey = int.MaxValue;

    public static Segment[] Align(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new,
        LocalMatches? local, IReadOnlyList<Segment>? block, DpOptions? options = null)
        => Align(old, @new, local, block, options, null);

    /// <summary>
    /// Shortest path through the pruned graph. <paramref name="local"/> == null is block-only mode;
    /// <paramref name="block"/> == null is local-only mode. Returns a canonical alignment.
    /// </summary>
    public static Segment[] Align(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new,
        LocalMatches? local, IReadOnlyList<Segment>? block, DpOptions? options, DpDiagnostics? diagnostics)
    {
        options ??= new DpOptions();
        options.Check();
        int n = old.Length, m = @new.Length;
        if (local != null && (local.OldPos.Length != m || local.Length.Length != m))
            throw new ArgumentException("local matches do not cover the new file", nameof(local));
        if (block != null)
            AlignmentUtil.Validate(block, n, m);
        if (m == 0)
        {
            if (diagnostics != null) diagnostics.Cost = 0;
            return Array.Empty<Segment>();
        }

        var dp = new Engine(options, n, m, local, block);
        return dp.Run(old, @new, diagnostics);
    }

    /// <summary>
    /// Cost of <paramref name="alignment"/> under the cost model of <paramref name="options"/>, exactly
    /// as the DP charges it: byte costs (0 / MismatchCost for matched bytes, UnmatchedCost for
    /// unmatched ones) plus SwitchCost at every position where the state changes (a matched segment
    /// followed by one with a different offset, or matched ↔ unmatched).
    /// </summary>
    public static long PathCost(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new,
        IReadOnlyList<Segment> alignment, DpOptions? options = null)
    {
        options ??= new DpOptions();
        AlignmentUtil.Validate(alignment, old.Length, @new.Length);
        long cost = 0;
        long prevKey = long.MinValue;
        for (int s = 0; s < alignment.Count; s++)
        {
            Segment seg = alignment[s];
            long key = seg.IsMatched ? seg.Offset : UnmatchedKey;
            if (s > 0 && key != prevKey) cost += options.SwitchCost;
            prevKey = key;
            if (seg.IsMatched)
            {
                ReadOnlySpan<byte> a = old.Slice(seg.OldPos, seg.Length), b = @new.Slice(seg.NewPos, seg.Length);
                int mismatches = 0;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) mismatches++;
                cost += (long)mismatches * options.MismatchCost;
            }
            else
            {
                cost += (long)seg.Length * options.UnmatchedCost;
            }
        }
        return cost;
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>Open-addressing (linear probing) map offset → state slot, with deletion.</summary>
    private sealed class OffsetMap
    {
        private readonly int[] _keys, _vals;     // _vals[i] < 0: empty
        private readonly int _mask;

        public OffsetMap(int capacity)
        {
            int size = 16;
            while (size < capacity * 4) size <<= 1;
            _keys = new int[size];
            _vals = new int[size];
            _vals.AsSpan().Fill(-1);
            _mask = size - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Home(int key) => (int)(((uint)key * 0x9E3779B1u) >> 16) & _mask;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Find(int key)
        {
            int i = Home(key);
            while (true)
            {
                int v = _vals[i];
                if (v < 0) return -1;
                if (_keys[i] == key) return v;
                i = (i + 1) & _mask;
            }
        }

        /// <summary>Adds a key that is not present.</summary>
        public void Add(int key, int val)
        {
            int i = Home(key);
            while (_vals[i] >= 0) i = (i + 1) & _mask;
            _keys[i] = key;
            _vals[i] = val;
        }

        /// <summary>Removes a key that is present (backward-shift deletion, no tombstones).</summary>
        public void Remove(int key)
        {
            int i = Home(key);
            while (_keys[i] != key || _vals[i] < 0) i = (i + 1) & _mask;
            int j = i;
            while (true)
            {
                j = (j + 1) & _mask;
                if (_vals[j] < 0) break;
                int h = Home(_keys[j]);
                // move j into the hole at i unless its home lies cyclically in (i, j]
                bool stays = i <= j ? (i < h && h <= j) : (i < h || h <= j);
                if (stays) continue;
                _keys[i] = _keys[j];
                _vals[i] = _vals[j];
                i = j;
            }
            _vals[i] = -1;
        }
    }

    /// <summary>
    /// Reference-counted run records {offset, startPos, parent, refCount} in pooled struct-of-arrays
    /// storage. A record is referenced by every live state that sits on it and by every child record.
    /// </summary>
    private sealed class RecordPool
    {
        public int[] Off = new int[1024], Start = new int[1024], Parent = new int[1024], Rc = new int[1024];
        private int _free = -1, _used;
        public int Live, Peak;

        public int New(int off, int start, int parent)
        {
            int r;
            if (_free >= 0) { r = _free; _free = Parent[r]; }
            else
            {
                if (_used == Off.Length) Grow();
                r = _used++;
            }
            Off[r] = off;
            Start[r] = start;
            Parent[r] = parent;
            Rc[r] = 1;
            if (parent >= 0) Rc[parent]++;
            if (++Live > Peak) Peak = Live;
            return r;
        }

        public void Release(int r)
        {
            while (r >= 0 && --Rc[r] == 0)
            {
                int p = Parent[r];
                Parent[r] = _free;
                _free = r;
                Live--;
                r = p;
            }
        }

        public int Capacity => Off.Length;

        public void AddRef(int r) => Rc[r]++;

        private void Grow()
        {
            int size = Off.Length * 2;
            Array.Resize(ref Off, size);
            Array.Resize(ref Start, size);
            Array.Resize(ref Parent, size);
            Array.Resize(ref Rc, size);
        }
    }

    /// <summary>
    /// The DP. Offset states live in stable slots (offset, distance, run record) that persist while
    /// the state stays in the candidate set, so a continuing state costs one byte comparison and a
    /// min; only offsets entering or leaving touch the offset → slot map. <c>_order</c> lists the
    /// live slots sorted by (distance, offset), the unmatched state included.
    /// </summary>
    private sealed unsafe class Engine
    {
        private readonly int _n, _m, _best, _window, _minLen, _mis, _unm, _sw;
        private readonly int[]? _localOld, _localLen;
        private readonly IReadOnlyList<Segment>? _block;
        private readonly RecordPool _pool = new RecordPool();

        private readonly int[] _off, _rec, _freeSlots;
        private readonly long[] _dist;
        private readonly byte[] _mark;            // per slot: 0 drop, 1 continue, 2 entered this step
        private int _freeCount;
        private readonly OffsetMap _map;
        private int[] _order, _nextOrder;
        private int _count;

        // distinct offsets of the local-match window, with multiplicities
        private readonly int[] _wOff, _wCnt;
        private int _wc;

        public Engine(DpOptions o, int n, int m, LocalMatches? local, IReadOnlyList<Segment>? block)
        {
            _n = n; _m = m;
            _best = o.BestCandidates;
            _window = local == null ? 0 : o.LocalCandidates;
            _minLen = Math.Max(1, o.MinLocalMatchLength);
            _mis = o.MismatchCost; _unm = o.UnmatchedCost; _sw = o.SwitchCost;
            _localOld = local?.OldPos; _localLen = local?.Length;
            _block = block;
            int cap = _best + _window + 2;     // live states at one position
            int slots = 2 * cap + 2;           // previous states plus entering ones
            _off = new int[slots];
            _rec = new int[slots];
            _dist = new long[slots];
            _mark = new byte[slots];
            _freeSlots = new int[slots];
            for (int i = 0; i < slots; i++) _freeSlots[i] = slots - 1 - i;
            _freeCount = slots;
            _map = new OffsetMap(slots);
            _order = new int[slots];
            _nextOrder = new int[slots];
            _wOff = new int[Math.Max(1, _window)];
            _wCnt = new int[Math.Max(1, _window)];
        }

        private void WindowAdd(int t)
        {
            if (_localLen![t] < _minLen) return;
            int o = _localOld![t] - t;
            for (int i = 0; i < _wc; i++)
                if (_wOff[i] == o) { _wCnt[i]++; return; }
            _wOff[_wc] = o;
            _wCnt[_wc++] = 1;
        }

        private void WindowRemove(int t)
        {
            if (_localLen![t] < _minLen) return;
            int o = _localOld![t] - t;
            for (int i = 0; i < _wc; i++)
            {
                if (_wOff[i] == o)
                {
                    if (--_wCnt[i] == 0)
                    {
                        _wc--;
                        _wOff[i] = _wOff[_wc];
                        _wCnt[i] = _wCnt[_wc];
                    }
                    return;
                }
            }
        }

        /// <summary>Creates a state entering at distance <paramref name="dist"/> (before its byte cost).</summary>
        private int NewSlot(int off, long dist, int rec)
        {
            int slot = _freeSlots[--_freeCount];
            _off[slot] = off;
            _dist[slot] = dist;
            _rec[slot] = rec;
            _mark[slot] = 2;
            if (off != UnmatchedKey) _map.Add(off, slot);
            return slot;
        }

        public Segment[] Run(ReadOnlySpan<byte> oldSpan, ReadOnlySpan<byte> newSpan, DpDiagnostics? diag)
        {
            fixed (byte* old = oldSpan)
            fixed (byte* nw = newSpan)
                return Run(old, nw, diag);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private Segment[] Run(byte* old, byte* nw, DpDiagnostics? diag)
        {
            int n = _n, m = _m;
            int blockIdx = 0;
            int peakStates = 0;
            long totalStates = 0;
            int[] off = _off, rec = _rec;
            long[] dist = _dist;
            byte[] mark = _mark;

            for (int t = 0; t < Math.Min(_window, m); t++) WindowAdd(t);

            // ---- position 0: every candidate starts at distance 0 plus its own byte cost
            {
                byte b = nw[0];
                for (int w = 0; w < _wc; w++)
                {
                    int o = _wOff[w];
                    if ((uint)o >= (uint)n || _map.Find(o) >= 0) continue;
                    _order[_count++] = NewSlot(o, old[o] == b ? 0 : _mis, _pool.New(o, 0, -1));
                }
                int bo = BlockOffset(0, ref blockIdx);
                if (bo != UnmatchedKey && (uint)bo < (uint)n && _map.Find(bo) < 0)
                    _order[_count++] = NewSlot(bo, old[bo] == b ? 0 : _mis, _pool.New(bo, 0, -1));
                _order[_count++] = NewSlot(UnmatchedKey, _unm, _pool.New(UnmatchedKey, 0, -1));
                SortOrder(_order, _count);
                peakStates = _count;
                totalStates = _count;
            }

            for (int pos = 1; pos < m; pos++)
            {
                if (_window > 0)
                {
                    WindowRemove(pos - 1);
                    if (pos + _window - 1 < m) WindowAdd(pos + _window - 1);
                }

                int bestSlot = _order[0];
                long sw = dist[bestSlot] + _sw;
                int bestRec = rec[bestSlot];
                _pool.AddRef(bestRec);              // keep it alive while states are dropped
                byte b = nw[pos];
                int limit = n - pos;                // offset o is valid at pos iff -pos <= o < limit

                // (2) + (4): the BestCandidates lowest-distance valid offsets, and unmatched
                int prevCount = _count, taken = 0;
                for (int r = 0; r < prevCount; r++)
                {
                    int i = _order[r];
                    int o = off[i];
                    if (o == UnmatchedKey) mark[i] = 1;
                    else if (taken < _best && o < limit) { mark[i] = 1; taken++; }
                    else mark[i] = 0;
                }

                // (1) offsets of the longest matches at pos … pos+window-1; (3) block prediction
                int entered = 0;
                for (int w = 0; w < _wc; w++)
                {
                    int o = _wOff[w];
                    if (o >= limit || o < -pos) continue;
                    int i = _map.Find(o);
                    if (i >= 0) { if (mark[i] == 0) mark[i] = 1; }
                    else _nextOrder[prevCount + entered++] = NewSlot(o, sw, _pool.New(o, pos, bestRec));
                }
                int bo = BlockOffset(pos, ref blockIdx);
                if (bo != UnmatchedKey && bo < limit && bo >= -pos)
                {
                    int i = _map.Find(bo);
                    if (i >= 0) { if (mark[i] == 0) mark[i] = 1; }
                    else _nextOrder[prevCount + entered++] = NewSlot(bo, sw, _pool.New(bo, pos, bestRec));
                }

                // update the continuing states in their old order, drop the others
                int kept = 0;
                for (int r = 0; r < prevCount; r++)
                {
                    int i = _order[r];
                    int o = off[i];
                    if (mark[i] == 0)
                    {
                        _map.Remove(o);
                        _pool.Release(rec[i]);
                        _freeSlots[_freeCount++] = i;
                        continue;
                    }
                    long d = dist[i];
                    if (sw < d)                     // ties stay on the same offset
                    {
                        d = sw;
                        int nr = _pool.New(o, pos, bestRec);
                        _pool.Release(rec[i]);
                        rec[i] = nr;
                    }
                    int cost = o == UnmatchedKey ? _unm : old[pos + o] == b ? 0 : _mis;
                    dist[i] = d + cost;
                    _nextOrder[kept++] = i;
                }
                for (int e = 0; e < entered; e++)
                {
                    int i = _nextOrder[prevCount + e];
                    dist[i] += old[pos + off[i]] == b ? 0 : _mis;
                    _nextOrder[kept++] = i;
                }
                _pool.Release(bestRec);

                (_order, _nextOrder) = (_nextOrder, _order);
                _count = kept;
                SortOrder(_order, kept);
                if (kept > peakStates) peakStates = kept;
                totalStates += kept;
            }

            // ---- traceback from the best final state
            int final = _order[0];
            if (diag != null)
            {
                diag.Cost = dist[final];
                diag.PeakLiveRecords = _pool.Peak;
                diag.FinalLiveRecords = _pool.Live;
                diag.RecordPoolCapacity = _pool.Capacity;
                diag.PeakStates = peakStates;
                diag.TotalStates = totalStates;
            }

            var runs = new List<Segment>();
            int end = m;
            for (int r = rec[final]; r >= 0; r = _pool.Parent[r])
            {
                int start = _pool.Start[r];
                if (end > start)
                {
                    int o = _pool.Off[r];
                    runs.Add(new Segment(start, end - start, o == UnmatchedKey ? Segment.Unmatched : start + o));
                }
                end = start;
            }
            runs.Reverse();
            return AlignmentUtil.Canonicalize(runs);
        }

        private int BlockOffset(int pos, ref int idx)
        {
            if (_block == null) return UnmatchedKey;
            while (_block[idx].NewEnd <= pos) idx++;
            Segment s = _block[idx];
            return s.IsMatched ? s.OldPos - s.NewPos : UnmatchedKey;
        }

        /// <summary>Insertion sort of slots by (distance, offset); the input is nearly sorted.</summary>
        private void SortOrder(int[] ord, int count)
        {
            long[] dist = _dist;
            int[] off = _off;
            for (int i = 1; i < count; i++)
            {
                int x = ord[i];
                long dx = dist[x];
                int y = ord[i - 1];
                long dy = dist[y];
                if (dy < dx || (dy == dx && off[y] < off[x])) continue;
                int ox = off[x];
                int j = i - 1;
                while (true)
                {
                    ord[j + 1] = y;
                    if (--j < 0) break;
                    y = ord[j];
                    dy = dist[y];
                    if (dy < dx || (dy == dx && off[y] < ox)) break;
                }
                ord[j + 1] = x;
            }
        }
    }
}
