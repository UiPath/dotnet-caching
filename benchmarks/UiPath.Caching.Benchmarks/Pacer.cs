using System.Diagnostics;

namespace UiPath.Caching.Benchmarks;

/// <summary>Waits for open-loop dispatchers: Thread.Sleep(1) lasts about 15 ms on Windows, so only a long gap sleeps and the last stretch spins.</summary>
internal static class Pacer
{
    private const int SleepAboveMs = 25;
    private const int SleepMarginMs = 16;

    public static void Pause(long remainingTicks)
    {
        var ms = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
        if (ms > SleepAboveMs)
        {
            Thread.Sleep(ms - SleepMarginMs);
        }
        else
        {
            Thread.SpinWait(20);
        }
    }
}
