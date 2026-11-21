namespace BsDiff6.Format;

/// <summary>
/// Thrown by <see cref="PatchReader"/> for any malformed, truncated, hostile or mismatching patch
/// (including a patch that does not belong to the supplied old file).
/// </summary>
public sealed class PatchFormatException : Exception
{
    public PatchFormatException(string message) : base(message) { }
    public PatchFormatException(string message, Exception inner) : base(message, inner) { }
}
