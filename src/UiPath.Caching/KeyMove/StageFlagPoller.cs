using System.Collections.Concurrent;

namespace UiPath.Caching.KeyMove;

/// <summary>Polls the stage source on its own loop, every 2 seconds in S0 to S4 and every 60 otherwise; a null or failed read keeps the last view and each call is bounded by one interval.</summary>
/// <param name="isActive">True for a group in S0 to S4 that is Init-ed or listed.</param>
internal sealed class StageFlagPoller(IMigrationStageSource source, Func<string, bool> isActive, KeyMoveStateOptions options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, int> _views = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;
    private Task[] _loops = [];

    /// <summary>The last value read, null before the first successful read: a pod with no view advertises no <c>fl</c>.</summary>
    public int? ViewOf(string group) => _views.TryGetValue(group, out var value) ? value : null;

    public TimeSpan IntervalFor(string group)
    {
        try
        {
            return isActive(group) ? options.PollInterval : options.SlowPollInterval;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return options.SlowPollInterval;
        }
    }

    public void Start(IEnumerable<string> groups, CancellationToken cancellationToken)
    {
        if (_stop is not null)
        {
            throw new InvalidOperationException("The poller is already running; stop it before it is started again.");
        }

        // The groups and the token are taken now: a caller that changes its collection or stops this poller later must not reach into the loops.
        var snapshot = groups.Distinct(StringComparer.Ordinal).ToArray();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _stop = stop;
        _loops = [.. snapshot.Select(g => Task.Run(() => RunAsync(g, stop.Token), CancellationToken.None))];
    }

    public async Task StopAsync()
    {
        if (_stop is null)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_loops).ConfigureAwait(false);
        _stop.Dispose();
        _stop = null;
    }

    /// <summary>One read, bounded by the group's poll interval; true when it produced a value.</summary>
    public async Task<bool> StepAsync(string group, CancellationToken cancellationToken)
    {
        var bound = IntervalFor(group);
        using var timeout = new CancellationTokenSource(bound, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            // The source sees the timeout token, so a call that honours it stops; one that does not is left behind by the wait.
            var value = await source.GetStageAsync(group, linked.Token).AsTask().WaitAsync(bound, time, cancellationToken).ConfigureAwait(false);
            if (value is >= 0 and <= 5)
            {
                _views[group] = value.Value;
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A failed or hung read is "could not read": the last view stays.
        }

        return false;
    }

    private async Task RunAsync(string group, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await StepAsync(group, cancellationToken).ConfigureAwait(false);
                await Task.Delay(IntervalFor(group), time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
