namespace BsDiff6.Cli;

internal static class Program
{
    private static int Main(string[] args) => BsPatchCli.Run(args, Console.Out, Console.Error);
}
