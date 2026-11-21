using System.Security.Cryptography;

namespace BsDiff6.Format;

/// <summary>Applies bsdiff 6 patches. The patch is treated as hostile (DESIGN 7.8).</summary>
public static class PatchReader
{
    /// <summary>
    /// Parses and structurally checks the header of <paramref name="patch"/> without applying it.
    /// Throws <see cref="PatchFormatException"/> if the header is malformed or the section sizes
    /// do not exactly fill the patch.
    /// </summary>
    public static PatchInfo ReadInfo(ReadOnlySpan<byte> patch) => PatchHeader.Read(patch);

    /// <summary>
    /// Reconstructs the new file. Throws <see cref="PatchFormatException"/> on any malformed,
    /// truncated or mismatching input; no other exception escapes for any patch bytes.
    /// </summary>
    public static byte[] Apply(ReadOnlySpan<byte> old, ReadOnlySpan<byte> patch)
    {
        PatchInfo h = PatchHeader.Read(patch);
        if (h.OldSize != old.Length) throw new PatchFormatException("old file has the wrong size for this patch");
        if (!MD5.HashData(old).AsSpan().SequenceEqual(h.OldMd5))
            throw new PatchFormatException("old file does not match this patch (MD5)");

        int pos = h.HeaderLength;
        ReadOnlySpan<byte> ctrlSec = patch.Slice(pos, (int)h.ControlCompressedLength); pos += ctrlSec.Length;
        ReadOnlySpan<byte> nzSec = patch.Slice(pos, (int)h.NonZeroCompressedLength); pos += nzSec.Length;
        ReadOnlySpan<byte> mapSec = patch.Slice(pos, (int)h.MapCompressedLength); pos += mapSec.Length;
        ReadOnlySpan<byte> extraSec = patch.Slice(pos, (int)h.ExtraCompressedLength);

        int newSize = (int)h.NewSize;
        // A record is at most 3 * 9 bytes and must produce at least one byte.
        if (h.ControlLength > 27L * newSize) throw new PatchFormatException("control string too long for the new size");
        byte[] control = StringCodec.Decompress(ctrlSec, (int)h.ControlCodec, h.ControlLength, "control string");

        // Pass 1: validate every record before allocating anything sized by the header.
        long d = 0, extraTotal = 0;
        var cr = new ControlReader(control, old.Length, newSize);
        while (cr.Next(out _, out int copyLen, out int extraLen))
        {
            d += copyLen;
            extraTotal += extraLen;
        }
        if (extraTotal != h.ExtraLength) throw new PatchFormatException("control string and extra length disagree");
        if (h.NonZeroLength > d) throw new PatchFormatException("non-zero string longer than the difference string");
        int dLen = (int)d;
        int k = (int)h.NonZeroLength;

        byte[] nz = StringCodec.Decompress(nzSec, (int)h.NonZeroCodec, h.NonZeroLength, "non-zero string");
        byte[] map = DecodeMap(mapSec, dLen, k, h.MapModel, h.BwtPrimary);
        byte[] extra = StringCodec.Decompress(extraSec, (int)h.ExtraCodec, h.ExtraLength, "extra string");

        // Pass 2: produce the output (all bounds were established in pass 1).
        byte[] output = new byte[newSize];
        int outPos = 0, mapPos = 0, nzPos = 0, extraPos = 0;
        cr = new ControlReader(control, old.Length, newSize);
        while (cr.Next(out int from, out int copyLen, out int extraLen))
        {
            DifferenceCoder.Apply(h.DiffMode, old.Slice(from, copyLen), map.AsSpan(mapPos, copyLen), nz, ref nzPos,
                output.AsSpan(outPos, copyLen));
            mapPos += copyLen;
            outPos += copyLen;
            Buffer.BlockCopy(extra, extraPos, output, outPos, extraLen);
            extraPos += extraLen;
            outPos += extraLen;
        }

        if (!MD5.HashData(output).AsSpan().SequenceEqual(h.NewMd5))
            throw new PatchFormatException("reconstructed file fails its MD5 check");
        return output;
    }

    /// <summary>
    /// Walks the control string, checking: well-formed varints, no record that produces nothing,
    /// cursor arithmetic in range (<c>0 &lt;= cursor &lt;= n - copyLen</c>), running output total
    /// never above newSize, and exactly newSize at the end.
    /// </summary>
    private ref struct ControlReader
    {
        private readonly ReadOnlySpan<byte> _control;
        private readonly int _oldLength, _newSize;
        private int _p;
        private long _cursor, _produced;

        public ControlReader(ReadOnlySpan<byte> control, int oldLength, int newSize)
        {
            _control = control;
            _oldLength = oldLength;
            _newSize = newSize;
            _p = 0;
            _cursor = 0;
            _produced = 0;
        }

        public bool Next(out int copyFrom, out int copyLen, out int extraLen)
        {
            if (_p >= _control.Length)
            {
                if (_produced != _newSize) throw new PatchFormatException("control string produces too little output");
                copyFrom = copyLen = extraLen = 0;
                return false;
            }
            if (!VarInt.TryReadSigned(_control, ref _p, out long seek)
                || !VarInt.TryRead(_control, ref _p, out ulong copyU)
                || !VarInt.TryRead(_control, ref _p, out ulong extraU))
                throw new PatchFormatException("control string: malformed or truncated record");
            if (copyU > PatchHeader.MaxLength || extraU > PatchHeader.MaxLength)
                throw new PatchFormatException("control string: length too large");
            if (copyU == 0 && extraU == 0) throw new PatchFormatException("control string: empty record");
            if (seek < -(long)int.MaxValue || seek > int.MaxValue)
                throw new PatchFormatException("control string: seek out of range");
            _cursor += seek;
            if (_cursor < 0 || _cursor > _oldLength - (long)copyU)
                throw new PatchFormatException("control string: copy outside the old file");
            _produced += (long)copyU + (long)extraU;
            if (_produced > _newSize) throw new PatchFormatException("control string produces too much output");
            copyFrom = (int)_cursor;
            copyLen = (int)copyU;
            extraLen = (int)extraU;
            _cursor += copyLen;
            return true;
        }
    }

    /// <summary>Decodes the difference map section into D bytes of 0/1.</summary>
    internal static byte[] DecodeMap(ReadOnlySpan<byte> mapSec, int d, int k, int model, long primary)
    {
        if (MidpointCoder.IsTrivial(d, k))
        {
            if (primary != 0) throw new PatchFormatException("BWT primary index must be 0 when the map section is empty");
            if (mapSec.Length != 0) throw new PatchFormatException("difference map section should be empty");
            byte[] m = new byte[d];
            if (k == d) m.AsSpan().Fill(1);
            return m;
        }
        if (primary < 1 || primary > d) throw new PatchFormatException("BWT primary index out of range");
        ulong[] l = MidpointCoder.Decode(mapSec, d, k, model);
        byte[] map = new byte[d];
        Bwt.Inverse(l, d, k, (int)primary, map);
        return map;
    }
}
