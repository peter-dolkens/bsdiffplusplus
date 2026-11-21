using System.IO.Compression;
using ICSharpCode.SharpZipLib.BZip2;

namespace BsDiff6.Format;

/// <summary>Compression of the control, non-zero and extra strings (DESIGN 7.6). The value is the header id.</summary>
public enum Codec
{
    None = 0,
    Zlib = 1,
    Bzip2 = 2,
}

internal static class StringCodec
{
    public static bool IsKnown(int id) => id >= 0 && id <= 2;

    public static byte[] Compress(ReadOnlySpan<byte> data, Codec codec)
    {
        switch (codec)
        {
            case Codec.None:
                return data.ToArray();
            case Codec.Zlib:
            {
                var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                    z.Write(data);
                return ms.ToArray();
            }
            case Codec.Bzip2:
            {
                var ms = new MemoryStream();
                using (var bz = new BZip2OutputStream(ms, 9) { IsStreamOwner = false })
                    bz.Write(data);
                return ms.ToArray();
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(codec));
        }
    }

    /// <summary>
    /// Compresses with <paramref name="forced"/>, or with every codec keeping the smallest
    /// (ties go to the lower id). <paramref name="sizes"/> receives each tried codec's size
    /// (-1 when not tried).
    /// </summary>
    public static byte[] CompressBest(ReadOnlySpan<byte> data, Codec? forced, out Codec chosen, long[] sizes)
    {
        for (int i = 0; i < sizes.Length; i++) sizes[i] = -1;
        if (forced.HasValue)
        {
            chosen = forced.Value;
            byte[] r = Compress(data, chosen);
            sizes[(int)chosen] = r.Length;
            return r;
        }
        byte[]? best = null;
        chosen = Codec.None;
        for (int c = 0; c <= 2; c++)
        {
            byte[] r = Compress(data, (Codec)c);
            sizes[c] = r.Length;
            if (best == null || r.Length < best.Length) { best = r; chosen = (Codec)c; }
        }
        return best!;
    }

    /// <summary>
    /// Decompresses <paramref name="src"/> which must expand to exactly <paramref name="declaredLength"/>
    /// bytes. Output is grown incrementally, so memory use follows the data actually present, and
    /// decompression stops at <c>declaredLength + 1</c> bytes.
    /// </summary>
    public static byte[] Decompress(ReadOnlySpan<byte> src, int codecId, long declaredLength, string what)
    {
        if (declaredLength < 0 || declaredLength > PatchHeader.MaxLength)
            throw new PatchFormatException(what + ": declared length out of range");
        switch (codecId)
        {
            case 0:
                if (src.Length != declaredLength) throw new PatchFormatException(what + ": stored length mismatch");
                return src.ToArray();
            case 1:
                // A deflate stream expands at most ~1032:1; reject impossible claims up front.
                if (declaredLength > 1100L * src.Length + 64)
                    throw new PatchFormatException(what + ": declared length impossible for its zlib data");
                return Inflate(src, codecId, (int)declaredLength, what);
            case 2:
                return Inflate(src, codecId, (int)declaredLength, what);
            default:
                throw new PatchFormatException(what + ": unknown codec " + codecId);
        }
    }

    /// <summary>
    /// Decompresses a zlib (codec 1) or bzip2 (codec 2) section. The codec must consume the
    /// section exactly: a stream that ends before the section does (trailing bytes), or a section
    /// that ends before its stream does, is malformed (DESIGN 7.8).
    /// </summary>
    private static byte[] Inflate(ReadOnlySpan<byte> src, int codecId, int declared, string what)
    {
        byte[] input = src.ToArray();
        long cap = (long)declared + 1;
        byte[] buf = new byte[(int)Math.Min(cap, Math.Max(4096L, Math.Min(4L * input.Length + 4096, 1L << 20)))];
        int n = 0;
        try
        {
            if (codecId == 1)
            {
                // SharpZipLib's Inflater rather than ZLibStream: it reports how much input is left
                // after the end of the stream, which ZLibStream (buffered reads) cannot.
                // .NET's ZLibStream writes nothing at all for empty input, so an empty section
                // is the empty string (as with the ZLibStream-based reader this replaced).
                if (input.Length == 0)
                {
                    if (declared != 0) throw new PatchFormatException(what + ": zlib stream truncated");
                    return Array.Empty<byte>();
                }
                var inf = new ICSharpCode.SharpZipLib.Zip.Compression.Inflater(noHeader: false);
                inf.SetInput(input);
                while (n < cap)
                {
                    if (n == buf.Length) Array.Resize(ref buf, (int)Math.Min(cap, (long)buf.Length * 2));
                    int got = inf.Inflate(buf, n, buf.Length - n);
                    if (got > 0) { n += got; continue; }
                    if (inf.IsFinished) break;
                    if (inf.IsNeedingDictionary) throw new PatchFormatException(what + ": zlib stream needs a preset dictionary");
                    if (inf.IsNeedingInput) throw new PatchFormatException(what + ": zlib stream truncated");
                    throw new PatchFormatException(what + ": zlib stream made no progress");
                }
                if (n < cap && inf.RemainingInput != 0)
                    throw new PatchFormatException(what + ": trailing data after the zlib stream");
            }
            else
            {
                // BZip2InputStream reads its input one byte at a time and stops at the end-of-stream
                // marker and CRC, so the input position afterwards is exactly what the stream used.
                using var ms = new MemoryStream(input, writable: false);
                using (var s = new BZip2InputStream(ms) { IsStreamOwner = false })
                {
                    while (n < cap)
                    {
                        if (n == buf.Length) Array.Resize(ref buf, (int)Math.Min(cap, (long)buf.Length * 2));
                        int got = s.Read(buf, n, buf.Length - n);
                        if (got <= 0) break;
                        n += got;
                    }
                }
                if (n < cap && ms.Position != input.Length)
                    throw new PatchFormatException(what + ": trailing data after the bzip2 stream");
            }
        }
        catch (PatchFormatException) { throw; }
        catch (OutOfMemoryException) { throw; }
        catch (Exception e)
        {
            throw new PatchFormatException(what + ": corrupt compressed data", e);
        }
        if (n != declared) throw new PatchFormatException(what + ": decompressed length does not match the declared length");
        if (buf.Length != n) Array.Resize(ref buf, n);
        return buf;
    }
}
