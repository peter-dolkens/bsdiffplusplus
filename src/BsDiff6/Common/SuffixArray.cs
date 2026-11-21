namespace BsDiff6;

/// <summary>
/// Suffix sorting entry point shared by local alignment and the difference-map BWT.
/// Convention: suffixes are sorted lexicographically by unsigned byte value, and a suffix that is
/// a proper prefix of another sorts first (an implicit sentinel smaller than every byte).
/// </summary>
/// <remarks>Linear time: delegates to the SA-IS implementation in <c>Alignment/SaIs.cs</c>.</remarks>
public static class SuffixArray
{
    /// <summary>
    /// Writes the suffix array of <paramref name="text"/> into <paramref name="sa"/>, which must have
    /// the same length (an <see cref="ArgumentException"/> is thrown otherwise).
    /// </summary>
    public static void Build(ReadOnlySpan<byte> text, Span<int> sa)
    {
        if (sa.Length != text.Length) throw new ArgumentException("sa must have the same length as text");
        Alignment.SaIs.Build(text, sa);
    }
}
