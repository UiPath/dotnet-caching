using Microsoft.Extensions.Options;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The built-in health source: <c>INFO</c> of every node plus the mover's own p99 and timeouts.</summary>
internal sealed class RedisInfoHealthSource(RedisInfoWatch watch, MoverLatency latency, IOptionsMonitor<KeyMoveMoverOptions> monitor) : IKeyMoveHealthSource
{
    private readonly LastGoodOptions _options = LastGoodOptions.Over(monitor);

    public async ValueTask<KeyMoveHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        var o = _options.CurrentValue;
        var reading = await watch.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (reading.MemoryPause is { } memory)
        {
            return new KeyMoveHealth(KeyMoveHealthState.Red, "memory: " + memory);
        }

        var worst = new Worst();
        if (reading.Loads.Any(load => JudgeLoad(load, o, worst)))
        {
            return worst.Verdict;
        }

        var timeouts = latency.TakeTimeouts();
        if (worst.IsRed(timeouts >= o.TimeoutsRed, () => $"{timeouts} timeouts") || JudgeP99(o, worst))
        {
            return worst.Verdict;
        }

        worst.Amber(timeouts >= o.TimeoutsAmber, () => $"{timeouts} timeouts");
        return worst.Verdict;
    }

    private static bool JudgeLoad(NodeLoad load, KeyMoveMoverOptions o, Worst worst)
    {
        if (worst.IsRed(load.LinkDown, () => $"replica of {load.Unit} has its link down"))
        {
            return true;
        }

        if (load.CpuPercent is { } cpu)
        {
            if (worst.IsRed(cpu >= o.CpuRedPercent, () => $"cpu {cpu:F0}% on {load.Unit}"))
            {
                return true;
            }

            worst.Amber(cpu >= o.CpuAmberPercent, () => $"cpu {cpu:F0}% on {load.Unit}");
        }

        if (load.LagBytes is not { } lag)
        {
            return false;
        }

        if (worst.IsRed(lag >= o.ReplicationLagRedBytes, () => $"replica of {load.Unit} is {lag} bytes behind"))
        {
            return true;
        }

        worst.Amber(lag >= o.ReplicationLagAmberBytes, () => $"replica of {load.Unit} is {lag} bytes behind");
        return false;
    }

    private bool JudgeP99(KeyMoveMoverOptions o, Worst worst)
    {
        if (latency.P99(o.MoverP99Window) is not { } p99)
        {
            return false;
        }

        if (worst.IsRed(p99 >= o.MoverP99Red, () => $"mover p99 {p99.TotalMilliseconds:F0} ms"))
        {
            return true;
        }

        worst.Amber(p99 >= o.MoverP99Amber, () => $"mover p99 {p99.TotalMilliseconds:F0} ms");
        return false;
    }

    // The first red ends the judgement; the first amber is kept as the verdict when nothing is red.
    private sealed class Worst
    {
        private KeyMoveHealth? _red;
        private string _amber = string.Empty;

        public KeyMoveHealth Verdict => _red
            ?? (_amber.Length > 0 ? new KeyMoveHealth(KeyMoveHealthState.Amber, _amber) : new KeyMoveHealth(KeyMoveHealthState.Green, string.Empty));

        public bool IsRed(bool reached, Func<string> reason)
        {
            if (reached)
            {
                _red = new KeyMoveHealth(KeyMoveHealthState.Red, reason());
            }

            return reached;
        }

        public void Amber(bool reached, Func<string> reason)
        {
            if (reached && _amber.Length == 0)
            {
                _amber = reason();
            }
        }
    }
}
