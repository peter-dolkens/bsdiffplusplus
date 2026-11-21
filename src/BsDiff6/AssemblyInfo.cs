using System.Runtime.CompilerServices;

// The single test assembly and the dev harnesses (dev/*-harness) use internals of every area.
[assembly: InternalsVisibleTo("BsDiff6.Tests")]
[assembly: InternalsVisibleTo("matching-harness")]
[assembly: InternalsVisibleTo("AlignmentHarness")]
[assembly: InternalsVisibleTo("BsDiff6.FormatHarness")]
