using System.Globalization;
using BsDiff6.Format;

namespace BsDiff6.Cli;

/// <summary>
/// The bspatch6 command line: <c>bspatch6 oldfile newfile patchfile</c> or
/// <c>bspatch6 --info patchfile</c>.
/// </summary>
public static class BsPatchCli
{
    private const string Tool = "bspatch6";

    private const string HelpText =
@"usage: bspatch6 oldfile newfile patchfile
       bspatch6 --info patchfile

Applies a bsdiff 6 patch to oldfile and writes newfile. The old file is checked
against the MD5 in the patch before anything is written, and the result after.

options:
  --info       print the patch header and section sizes without applying it
  -h, --help   show this help
  --version    show the version

exit status: 0 success, 1 failure (I/O, malformed patch, wrong old file), 2 usage error";

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr) =>
        CliSupport.Guard(Tool, stderr, () => RunCore(args, stdout));

    private static int RunCore(string[] argv, TextWriter stdout)
    {
        bool info = false, optionsDone = false;
        var files = new List<string>();
        foreach (string s in argv)
        {
            if (optionsDone || s == "-" || !s.StartsWith("-", StringComparison.Ordinal)) { files.Add(s); continue; }
            switch (s)
            {
                case "--": optionsDone = true; break;
                case "--info": info = true; break;
                case "-h": case "--help": stdout.WriteLine(HelpText); return CliSupport.Success;
                case "--version": stdout.WriteLine($"{Tool} {CliSupport.Version} (patch format version 1)"); return CliSupport.Success;
                default: throw new UsageException($"unknown option '{s}'");
            }
        }

        if (info)
        {
            if (files.Count != 1) throw new UsageException("--info takes exactly one patch file");
            byte[] p = CliSupport.ReadInput(files[0], "patch file");
            PrintInfo(stdout, PatchReader.ReadInfo(p));
            return CliSupport.Success;
        }

        if (files.Count != 3) throw new UsageException(files.Count < 3 ? "missing operand" : "too many operands");
        byte[] old = CliSupport.ReadInput(files[0], "old file");
        byte[] patch = CliSupport.ReadInput(files[2], "patch file");
        byte[] @new = BsPatch.Apply(old, patch);
        CliSupport.WriteAtomically(files[1], @new);
        return CliSupport.Success;
    }

    private static void PrintInfo(TextWriter o, PatchInfo h)
    {
        var inv = CultureInfo.InvariantCulture;
        string N(long v) => v.ToString("N0", inv);
        o.WriteLine($"format       BSDIFF6+ version {h.Version}");
        o.WriteLine($"old size     {N(h.OldSize)}");
        o.WriteLine($"new size     {N(h.NewSize)}");
        o.WriteLine($"old MD5      {Convert.ToHexString(h.OldMd5).ToLowerInvariant()}");
        o.WriteLine($"new MD5      {Convert.ToHexString(h.NewMd5).ToLowerInvariant()}");
        o.WriteLine($"diff mode    {CliSupport.ModeName(h.DiffMode)}");
        o.WriteLine($"map model    {h.MapModel}");
        o.WriteLine($"BWT primary  {h.BwtPrimary}");
        o.WriteLine($"{"section",-12} {"raw",14} {"stored",12}  codec");
        o.WriteLine($"{"header",-12} {"",14} {N(h.HeaderLength),12}");
        o.WriteLine($"{"control",-12} {N(h.ControlLength),14} {N(h.ControlCompressedLength),12}  {CliSupport.CodecName(h.ControlCodec)}");
        o.WriteLine($"{"non-zero",-12} {N(h.NonZeroLength),14} {N(h.NonZeroCompressedLength),12}  {CliSupport.CodecName(h.NonZeroCodec)}");
        o.WriteLine($"{"map",-12} {"",14} {N(h.MapCompressedLength),12}  midpoint");
        o.WriteLine($"{"extra",-12} {N(h.ExtraLength),14} {N(h.ExtraCompressedLength),12}  {CliSupport.CodecName(h.ExtraCodec)}");
        o.WriteLine($"{"total",-12} {"",14} {N(h.TotalLength),12}");
    }
}
