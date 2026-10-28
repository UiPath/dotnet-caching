using System.Globalization;
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
        if (args.Length > 0 && args[0] == "hashmiss")
        {
            await HashMissHarness.RunAsync(args.Length > 1 ? args[1] : "RedisStreams", IntArg(args, 2, 10));
            return;
        }
        if (args.Length > 0 && args[0] == "redishit")
        {
            await RedisHitHarness.RunAsync(args.Length > 1 ? args[1] : "127.0.0.1:6379");
            return;
        }
        if (args.Length > 0 && args[0] == "hashwrite")
        {
            await HashWriteCostHarness.RunAsync(args.Length > 1 ? args[1] : "127.0.0.1:6379");
            return;
        }
        if (args.Length > 0 && args[0] == "peerstress")
        {
            await PeerUpdateStressHarness.RunAsync(args.Length > 1 ? args[1] : "127.0.0.1:6379", IntArg(args, 2, 60), IntArg(args, 3, 20), IntArg(args, 4, 16), IntArg(args, 5, 3));
            return;
        }
        if (args.Length > 0 && args[0] == "churn")
        {
            await ChurnLoadHarness.RunAsync(
                args.Length > 1 ? args[1] : "127.0.0.1:6379",
                args.Length > 2 ? args[2] : "b",
                IntArg(args, 3, 30),
                IntArg(args, 4, 2000),
                IntArg(args, 5, 1000),
                IntArg(args, 6, 10));
            return;
        }
        if (args.Length > 0 && args[0] == "marker")
        {
            await MarkerAfterInvalidationHarness.RunAsync(
                args.Length > 1 ? args[1] : "127.0.0.1:6379",
                IntArg(args, 2, 5850),
                IntArg(args, 3, 45),
                IntArg(args, 4, 30),
                IntArg(args, 5, 1000),
                args.Length > 6 ? double.Parse(args[6], CultureInfo.InvariantCulture) : 0.2,
                IntArg(args, 7, 5850));
            return;
        }
        if (args.Length > 0 && args[0] == "memmiss")
        {
            await MemoryMissHarness.RunAsync();
            return;
        }
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    private static int IntArg(string[] args, int index, int fallback) =>
        args.Length > index ? int.Parse(args[index], CultureInfo.InvariantCulture) : fallback;
}
