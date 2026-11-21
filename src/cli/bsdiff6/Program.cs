namespace BsDiff6.Cli;

internal static class Program
{
    private static int Main(string[] args) => BsDiffCli.Run(args, Console.Out, Console.Error);
}
