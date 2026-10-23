using System.Runtime.CompilerServices;

namespace UiPath.Caching.Tests;

internal static class ThreadPoolFloor
{
    // Many tests park pool threads on synchronous waits while others wait on timers and queued work; on a small runner
    // the pool adds threads too slowly for those and they wait for seconds.
    [ModuleInitializer]
    internal static void Raise()
    {
        ThreadPool.GetMinThreads(out var worker, out var completion);
        ThreadPool.SetMinThreads(Math.Max(worker, 64), Math.Max(completion, 64));
    }
}
