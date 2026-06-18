using BenchmarkDotNet.Running;

namespace UiPath.Caching.Benchmarks;
public class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "doorbell")
        {
            var durationSec = args.Length > 1 && int.TryParse(args[1], out var d) ? d : 20;
            var writeHz = args.Length > 2 && int.TryParse(args[2], out var w) ? w : 5;
            await StreamNotifyDoorbellHarness.RunAsync(durationSec, writeHz);
            return;
        }
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
