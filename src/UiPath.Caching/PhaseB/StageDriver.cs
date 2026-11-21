using System.Collections.Concurrent;

namespace UiPath.Caching.PhaseB;

/// <summary>The leader's loop for each group: reads the state and the fresh adverts, asks the step logic for the next step, and runs it, checking the lease before every write.</summary>
internal sealed partial class StageDriver(StageDriverServices services, StageDriverOptions options, PhaseBStateOptions stateOptions, FlagViews flags, ViewStability stability, TimeProvider time)
{
    private static readonly TimeSpan DirectAlertEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, GroupMemory> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _notified = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;
    private Task[] _loops = [];

    public static Func<Task<T>> Memoize<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        Task<T>? task = null;
        return () => task ??= read(cancellationToken);
    }

    public void Start(IEnumerable<string> groups, CancellationToken cancellationToken)
    {
        if (_stop is not null)
        {
            throw new InvalidOperationException("The driver is already running; stop it before it is started again.");
        }

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

    /// <summary>One step for one group; null when this node does not lead the group or the group has no state.</summary>
    public async Task<StepOutcome?> StepAsync(string group, CancellationToken cancellationToken)
    {
        if (!services.HoldsLease(group))
        {
            return null;
        }

        var memory = _memory.GetOrAdd(group, _ => new GroupMemory());
        var state = await services.Store.ReadAsync(group, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }

        var input = await InputAsync(group, state, memory, cancellationToken).ConfigureAwait(false);
        var plan = await StepPlanner.DecideAsync(input, options).ConfigureAwait(false);
        RaiseAlerts(group, memory, plan.AlertList);
        var result = await ExecuteAsync(group, state, input, plan, memory, cancellationToken).ConfigureAwait(false);
        return new StepOutcome(plan, result);
    }

    private async Task RunAsync(string group, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    await StepAsync(group, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Whatever failed, the group's loop goes on: a leader that stops stepping silently leaves the group where it is.
                    Notify(group, "step_failed", ex.Message);
                }

                await Task.Delay(options.StepInterval, time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped.
        }
    }

    private async Task<StepInput> InputAsync(string group, StateRecord state, GroupMemory memory, CancellationToken cancellationToken)
    {
        var fresh = await FreshAsync(cancellationToken).ConfigureAwait(false);
        var stage = StepPlanner.StageFlagOf(state);
        var verdict = flags.Sample(group, fresh, stage, state is { State: "S4", Direction: "fwd" } ? 5 : null);
        var stable = stability.Observe(group, fresh);
        var conflict = services.Monitor?.RulesFor(group).Alerts.FirstOrDefault(a => a is "identity_conflict" or "commit_mismatch");
        var zero = memory.DurZeroFor(state, time);
        var mover = services.Mover;
        var drain = Memoize(ct => mover.DrainAsync(group, state.Direction, ct), cancellationToken);
        var census = Memoize(ct => mover.ResidueCensusAsync(group, ct), cancellationToken);
        return new StepInput(
            group,
            state,
            fresh,
            verdict,
            stable,
            time.GetUtcNow(),
            zero,
            conflict,
            services.OwnScope,
            stateOptions.StaleAfter(stateOptions.PollInterval),
            services.Facts,
            approved => services.Preflight.CheckAsync(approved, cancellationToken),
            () => services.Data.CountNativeAsync(group, cancellationToken),
            drain,
            census);
    }

    private async Task<IReadOnlyList<NodeAdvert>> FreshAsync(CancellationToken cancellationToken) =>
        [.. (await services.Adverts.ReadAsync(cancellationToken).ConfigureAwait(false)).Where(a => a.Age <= options.AdvertFresh)];

    private LeaseFence? Fence(string group) => services.Fence?.Invoke(group);

    // The alerts the steps raise directly repeat on every step while their cause stands, so each is sent once a minute.
    private void Notify(string group, string code, string detail)
    {
        var key = $"{group}|{code}|{detail}";
        var now = time.GetTimestamp();
        if (_notified.TryGetValue(key, out var sent) && time.GetElapsedTime(sent, now) < DirectAlertEvery)
        {
            return;
        }

        _notified[key] = now;
        services.Alert?.Invoke(group, code, detail);
    }

    private void RaiseAlerts(string group, GroupMemory memory, IReadOnlyList<StageAlert> alerts)
    {
        var now = time.GetTimestamp();
        var current = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var alert in alerts)
        {
            var key = $"{alert.Code}|{alert.Detail}";
            var since = memory.Alerted.TryGetValue(key, out var earlier) ? earlier : (long?)null;
            var repeat = alert.Code == "flag_below_closed" && since is { } s && time.GetElapsedTime(s, now) >= options.RepeatAlertAfter;
            current[key] = since is not null && !repeat ? since.Value : now;
            if (since is null || repeat)
            {
                services.Alert?.Invoke(group, alert.Code, alert.Detail);
            }
        }

        memory.Alerted = current;
    }

    private sealed class GroupMemory
    {
        private string? _zeroIdentity;
        private long _zeroSince;

        public Dictionary<string, long> Alerted { get; set; } = [];

        /// <summary>The leader that started a transition that then waited at dur=0 counts the retry time from that start.</summary>
        public void Seed(string identity, TimeProvider time) => (_zeroIdentity, _zeroSince) = (identity, time.GetTimestamp());

        public TimeSpan? DurZeroFor(StateRecord state, TimeProvider time)
        {
            if (state.Durable)
            {
                _zeroIdentity = null;
                return null;
            }

            if (_zeroIdentity != state.Identity)
            {
                (_zeroIdentity, _zeroSince) = (state.Identity, time.GetTimestamp());
            }

            return time.GetElapsedTime(_zeroSince);
        }
    }
}
