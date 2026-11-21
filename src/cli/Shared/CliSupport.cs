using System.Reflection;
using BsDiff6.Format;

namespace BsDiff6.Cli;

/// <summary>Thrown for command-line usage errors (exit code 2).</summary>
internal sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}

/// <summary>Helpers shared by bsdiff6 and bspatch6 (linked into both).</summary>
internal static class CliSupport
{
    public const int Success = 0, Failure = 1, Usage = 2;

    public static string Version
    {
        get
        {
            Assembly lib = typeof(BsDiff).Assembly;
            string v = lib.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? lib.GetName().Version?.ToString() ?? "?";
            int plus = v.IndexOf('+');
            if (plus >= 0) v = v.Substring(0, plus);
            return v;
        }
    }

    /// <summary>
    /// Writes <paramref name="data"/> to <paramref name="path"/> through a temporary file in the
    /// same directory that is renamed over the destination only once it is complete, so a failure
    /// never leaves a truncated file under the destination name.
    /// </summary>
    public static void WriteAtomically(string path, byte[] data)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full) ?? ".";
        string tmp = Path.Combine(dir, "." + Path.GetFileName(full) + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    public static byte[] ReadInput(string path, string what)
    {
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) { throw new IOException($"{what} '{path}' not found"); }
        catch (DirectoryNotFoundException) { throw new IOException($"{what} '{path}' not found"); }
    }

    /// <summary>
    /// Runs <paramref name="body"/>, mapping exceptions to exit codes with a one-line message on
    /// <paramref name="stderr"/>: usage errors → 2, everything else → 1. No stack traces.
    /// </summary>
    public static int Guard(string tool, TextWriter stderr, Func<int> body)
    {
        try
        {
            return body();
        }
        catch (UsageException e)
        {
            stderr.WriteLine($"{tool}: {e.Message}");
            stderr.WriteLine($"Try '{tool} --help'.");
            return Usage;
        }
        catch (PatchFormatException e)
        {
            stderr.WriteLine($"{tool}: invalid patch: {e.Message}");
            return Failure;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is NotSupportedException
                                  || e is ArgumentException || e is OutOfMemoryException)
        {
            stderr.WriteLine($"{tool}: error: {OneLine(e.Message)}");
            return Failure;
        }
        catch (Exception e)
        {
            stderr.WriteLine($"{tool}: internal error ({e.GetType().Name}): {OneLine(e.Message)}");
            return Failure;
        }
    }

    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    public static string CodecName(Codec c) => c switch
    {
        Codec.None => "none",
        Codec.Zlib => "zlib",
        Codec.Bzip2 => "bzip2",
        _ => c.ToString(),
    };

    public static string ModeName(DiffMode m) => m switch
    {
        DiffMode.Correction => "correction",
        DiffMode.Bytewise => "bytewise",
        DiffMode.LittleEndian => "little-endian",
        DiffMode.BigEndian => "big-endian",
        _ => m.ToString(),
    };
}
