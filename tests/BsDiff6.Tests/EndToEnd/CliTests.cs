using System.Text.Json;
using BsDiff6.Cli;

namespace BsDiff6.Tests.EndToEnd;

/// <summary>The two command-line entry points, run in-process.</summary>
public sealed class CliTests : IDisposable
{
    private readonly string _dir;

    public CliTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bsdiff6-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string[] Split(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private string P(string name) => Path.Combine(_dir, name);

    private static (int code, string stdout, string stderr) Diff(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = BsDiffCli.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private static (int code, string stdout, string stderr) Patch(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = BsPatchCli.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private (byte[] old, byte[] @new) WriteSample()
    {
        var rng = new Random(5);
        byte[] old = E2EData.Structured(rng, 40_000);
        byte[] @new = E2EData.PlantEdits(rng, E2EData.Relocate(old, 0x10), 8);
        File.WriteAllBytes(P("old"), old);
        File.WriteAllBytes(P("new"), @new);
        return (old, @new);
    }

    private void AssertNoTempFiles() =>
        Assert.Empty(Directory.GetFiles(_dir, ".*tmp-*"));

    [Fact]
    public void DpCostsReachStatsAndVerbose()
    {
        WriteSample();
        var d = Diff("-v", "--no-prune", "--dp-costs", "3,2,40", "--stats-json", P("stats.json"), P("old"), P("new"), P("patch"));
        Assert.True(d.code == 0, d.stderr);
        Assert.Contains("prune=False", d.stderr);
        Assert.Contains("costs=3,2,40", d.stderr);
        using JsonDocument j = JsonDocument.Parse(File.ReadAllBytes(P("stats.json")));
        JsonElement opts = j.RootElement.GetProperty("options");
        Assert.False(opts.GetProperty("prune").GetBoolean());
        JsonElement c = opts.GetProperty("dp_costs");
        Assert.Equal(3, c.GetProperty("mismatch").GetInt32());
        Assert.Equal(2, c.GetProperty("unmatched").GetInt32());
        Assert.Equal(40, c.GetProperty("switch").GetInt32());
    }

    [Fact]
    public void RoundTripWithStats()
    {
        var (_, @new) = WriteSample();
        var d = Diff("-v", "--stats-json", P("stats.json"), P("old"), P("new"), P("patch"));
        Assert.True(d.code == 0, d.stderr);
        Assert.Contains("sections", d.stderr);
        Assert.Contains("difference modes", d.stderr);
        using (JsonDocument j = JsonDocument.Parse(File.ReadAllBytes(P("stats.json"))))
        {
            JsonElement root = j.RootElement;
            Assert.Equal(new FileInfo(P("patch")).Length, root.GetProperty("patch").GetProperty("size").GetInt64());
            Assert.Equal(root.GetProperty("patch").GetProperty("size").GetInt64(),
                         root.GetProperty("sections").GetProperty("total").GetInt64());
            Assert.Equal(4, root.GetProperty("diff_mode_candidates").GetArrayLength());
            Assert.True(root.GetProperty("alignment").GetProperty("matched_fraction").GetDouble() > 0.5);
            JsonElement opts = root.GetProperty("options");
            Assert.True(opts.GetProperty("prune").GetBoolean());
            Assert.Equal(14, opts.GetProperty("dp_costs").GetProperty("switch").GetInt32());
        }

        var p = Patch(P("old"), P("out"), P("patch"));
        Assert.True(p.code == 0, p.stderr);
        Assert.Equal(@new, File.ReadAllBytes(P("out")));

        var info = Patch("--info", P("patch"));
        Assert.Equal(0, info.code);
        Assert.Contains("extra", info.stdout);
        Assert.Contains("BSDIFF6+", info.stdout);
        AssertNoTempFiles();
    }

    [Theory]
    [InlineData("--block-only")]
    [InlineData("--no-block")]
    [InlineData("--prune")]
    [InlineData("--no-parallel")]
    [InlineData("--prune --min-match 4 --seed 0x1234")]
    [InlineData("--no-prune --seed 99 --")]
    [InlineData("--dp-costs 3,2,40")]
    [InlineData("--dp-costs 1,1,1 --no-block")]
    public void OptionsRoundTrip(string options)
    {
        var (_, @new) = WriteSample();
        var args = options.Split(' ').Concat(new[] { P("old"), P("new"), P("patch") }).ToArray();
        var d = Diff(args);
        Assert.True(d.code == 0, d.stderr);
        Assert.Equal("", d.stderr);
        Assert.Equal(0, Patch(P("old"), P("out"), P("patch")).code);
        Assert.Equal(@new, File.ReadAllBytes(P("out")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a b c d")]
    [InlineData("--bogus a b c")]
    [InlineData("--block-only --no-block a b c")]
    [InlineData("--min-match 0 a b c")]
    [InlineData("--min-match x a b c")]
    [InlineData("--seed -1 a b c")]
    [InlineData("a b c --stats-json")]
    [InlineData("--dp-costs 2,1 a b c")]
    [InlineData("--dp-costs 2,1,20,4 a b c")]
    [InlineData("--dp-costs 0,1,20 a b c")]
    [InlineData("--dp-costs 2,-1,20 a b c")]
    [InlineData("--dp-costs 2,x,20 a b c")]
    [InlineData("--dp-costs 2,,20 a b c")]
    [InlineData("a b c --dp-costs")]
    public void DiffUsageErrors(string argLine)
    {
        var d = Diff(Split(argLine));
        Assert.Equal(2, d.code);
        Assert.StartsWith("bsdiff6: ", d.stderr);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("--info")]
    [InlineData("--info a b")]
    [InlineData("-x a b c")]
    public void PatchUsageErrors(string argLine)
    {
        var p = Patch(Split(argLine));
        Assert.Equal(2, p.code);
        Assert.StartsWith("bspatch6: ", p.stderr);
    }

    [Fact]
    public void HelpAndVersion()
    {
        Assert.Equal(0, Diff("--help").code);
        Assert.Contains("usage", Diff("-h").stdout);
        Assert.Contains("bsdiff6 ", Diff("--version").stdout);
        Assert.Equal(0, Patch("--help").code);
        Assert.Contains("bspatch6 ", Patch("--version").stdout);
    }

    [Fact]
    public void RuntimeFailures()
    {
        var (old, _) = WriteSample();
        // Missing input.
        var d = Diff(P("nope"), P("new"), P("patch"));
        Assert.Equal(1, d.code);
        Assert.Single(d.stderr.Trim().Split('\n'));
        Assert.False(File.Exists(P("patch")));
        // Output directory does not exist.
        Assert.Equal(1, Diff(P("old"), P("new"), Path.Combine(_dir, "no", "such", "patch")).code);

        Assert.Equal(0, Diff(P("old"), P("new"), P("patch")).code);
        // Wrong old file: no output written.
        byte[] other = (byte[])old.Clone();
        other[100] ^= 1;
        File.WriteAllBytes(P("other"), other);
        var p = Patch(P("other"), P("out"), P("patch"));
        Assert.Equal(1, p.code);
        Assert.Contains("invalid patch", p.stderr);
        Assert.Single(p.stderr.Trim().Split('\n'));
        Assert.False(File.Exists(P("out")));
        // Malformed patch: an existing output file is left untouched.
        File.WriteAllBytes(P("out"), new byte[] { 1, 2, 3 });
        byte[] patch = File.ReadAllBytes(P("patch"));
        File.WriteAllBytes(P("bad"), patch.AsSpan(0, patch.Length - 3).ToArray());
        Assert.Equal(1, Patch(P("old"), P("out"), P("bad")).code);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(P("out")));
        Assert.Equal(1, Patch("--info", P("bad")).code);
        Assert.Equal(1, Patch("--info", P("nope")).code);
        Assert.Equal(1, Patch(P("old"), P("out"), P("nope")).code);
        AssertNoTempFiles();
    }
}
