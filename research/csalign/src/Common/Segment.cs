namespace BsDiff6;

/// <summary>
/// One run of an alignment of the new file against the old file: <see cref="Length"/> bytes of
/// the new file starting at <see cref="NewPos"/> are aligned with the old file starting at
/// <see cref="OldPos"/>, or are unmatched when <see cref="OldPos"/> is <see cref="Unmatched"/>.
/// </summary>
public readonly record struct Segment(int NewPos, int Length, int OldPos)
{
    public const int Unmatched = -1;

    public bool IsMatched => OldPos >= 0;

    /// <summary>Exclusive end of the run in the new file.</summary>
    public int NewEnd => NewPos + Length;

    /// <summary>oldPos - newPos. Only meaningful when <see cref="IsMatched"/>.</summary>
    public int Offset => OldPos - NewPos;
}

public static class AlignmentUtil
{
    /// <summary>
    /// Throws <see cref="ArgumentException"/> unless <paramref name="segments"/> is sorted, covers
    /// [0, newLength) exactly, has only positive lengths, and every matched segment lies inside
    /// [0, oldLength).
    /// </summary>
    public static void Validate(IReadOnlyList<Segment> segments, int oldLength, int newLength)
    {
        long pos = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            Segment s = segments[i];
            if (s.NewPos != pos)
                throw new ArgumentException($"segment {i} starts at {s.NewPos}, expected {pos}");
            if (s.Length <= 0)
                throw new ArgumentException($"segment {i} has length {s.Length}");
            if (s.OldPos < Segment.Unmatched)
                throw new ArgumentException($"segment {i} has old position {s.OldPos}");
            if (s.IsMatched && (long)s.OldPos + s.Length > oldLength)
                throw new ArgumentException($"segment {i} runs past the end of the old file");
            pos += s.Length;
        }
        if (pos != newLength)
            throw new ArgumentException($"segments cover {pos} bytes, expected {newLength}");
    }

    /// <summary>Merges adjacent segments that continue each other. Input must be valid.</summary>
    public static Segment[] Canonicalize(IReadOnlyList<Segment> segments)
    {
        var result = new List<Segment>(segments.Count);
        foreach (Segment s in segments)
        {
            if (result.Count > 0)
            {
                Segment last = result[^1];
                bool continues = last.IsMatched
                    ? s.IsMatched && s.Offset == last.Offset
                    : !s.IsMatched;
                if (continues)
                {
                    result[^1] = last with { Length = last.Length + s.Length };
                    continue;
                }
            }
            result.Add(s);
        }
        return result.ToArray();
    }
}
