using System.Collections.Concurrent;

namespace UiPath.Caching.KeyMove;

/// <summary>The snapshot loop of a node: reads the state of every registered group at startup (retrying until the answer is definitive), keeps relevant groups polled, and answers <see cref="RulesFor"/> from memory, so no hot path reads Redis.</summary>
/// <param name="alert">Told once each time a group's alert becomes active.</param>
internal sealed class GroupStateMonitor(IStateReader reader, KeyMoveStateOptions options, TimeProvider time, Action<string, string>? alert = null) : IAckedIdentitySource
{
    private readonly ConcurrentDictionary<string, GroupTracker> _groups = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;
    private Task[] _loops = [];

    public void Start(IEnumerable<GroupRegistration> registrations, CancellationToken cancellationToken)
    {
        if (_stop is not null)
        {
            throw new InvalidOperationException("The monitor is already running; stop it before it is started again.");
        }

        var list = registrations.ToArray();
        _ = Register(list);

        // A monitor that was stopped keeps its trackers, and every one of them is polled again.
        var trackers = list.Select(r => _groups[r.Name]).Distinct().ToArray();
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _stop.Token;
        _loops = [.. trackers.Select(t => Task.Run(() => RunAsync(t, token), CancellationToken.None))];
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
        _loops = [];
    }

    public GroupRules RulesFor(string group) =>
        _groups.TryGetValue(group, out var tracker) ? GroupRuleEvaluator.Evaluate(tracker.View(), options) : GroupRules.Unaffected;

    /// <summary>True once the node has made a definitive read of the group's state; false before it, and for a group the monitor does not track.</summary>
    public bool HasRead(string group) => _groups.TryGetValue(group, out var tracker) && tracker.View().SinceRead is not null;

    /// <summary>The state the node last read for the group, whether or not it acts on it yet; null for none.</summary>
    public StateRecord? CurrentState(string group) => _groups.TryGetValue(group, out var tracker) ? tracker.View().State : null;

    /// <summary>The age of the node's oldest snapshot among its fast-polled relevant groups; null when it has none, or when one has not read yet.</summary>
    public TimeSpan? SnapshotAge()
    {
        TimeSpan? oldest = null;
        foreach (var tracker in _groups.Values)
        {
            var view = tracker.View();
            if (!GroupRuleEvaluator.IsRelevant(view) || GroupRuleEvaluator.PollInterval(view, options) != options.PollInterval)
            {
                // A slow poll must not make the node look stale.
                continue;
            }

            if (view.SinceRead is not { } age)
            {
                return null;
            }

            oldest = oldest is { } held && held > age ? held : age;
        }

        return oldest;
    }

    public bool Acknowledge(string group, long ep, string id) =>
        _groups.TryGetValue(group, out var tracker) && tracker.Acknowledge(ep, id);

    public AckedIdentity? AckedFor(string group)
    {
        if (!_groups.TryGetValue(group, out var tracker) || tracker.Acknowledged is not { } acked)
        {
            return null;
        }

        var rules = GroupRuleEvaluator.Evaluate(tracker.View(), options);
        return rules.WithdrawAcks || rules.Alerts.Contains("identity_conflict") || rules.Alerts.Contains("commit_mismatch")
            ? null
            : new AckedIdentity(acked.Ep, acked.Id, acked.State, acked.Direction);
    }

    /// <summary>Returns null when the group needs no more polling.</summary>
    internal async Task<TimeSpan?> StepAsync(string group, CancellationToken cancellationToken)
    {
        var tracker = _groups[group];
        var first = tracker.View().Answer == StateAnswer.Unknown;
        var read = await ReadSnapshotAsync(group, tracker, first, cancellationToken).ConfigureAwait(false);
        RaiseAlerts(tracker);
        if (!read)
        {
            return options.RetryInterval;
        }

        var view = tracker.View();
        if (GroupRuleEvaluator.IsRelevant(view))
        {
            return GroupRuleEvaluator.PollInterval(view, options);
        }

        return tracker.Registration.Flagged ? options.SlowPollInterval : null;
    }

    internal List<GroupTracker> Register(IEnumerable<GroupRegistration> registrations)
    {
        var made = new List<GroupTracker>();
        foreach (var registration in registrations)
        {
            MoveKeyNames.ValidateName(registration.Name, nameof(registrations));
            var tracker = new GroupTracker(registration, time);
            if (_groups.TryAdd(registration.Name, tracker))
            {
                made.Add(tracker);
            }
        }

        return made;
    }

    private async Task<bool> ReadSnapshotAsync(string group, GroupTracker tracker, bool first, CancellationToken cancellationToken)
    {
        var stateRead = false;
        try
        {
            // Read records first so a concurrent transition can only make the state newer.
            var before = tracker.View();
            var sampling = first || before.Answer != StateAnswer.Present || before.Conflict is not null;
            var listed = tracker.Registration.Listed;
            ControlSample? sample = null;
            if (sampling)
            {
                sample = listed ? await reader.SampleAsync(group, cancellationToken).ConfigureAwait(false) : await reader.ProbeAsync(group, cancellationToken).ConfigureAwait(false);
            }

            stateRead = true;
            await ApplySnapshotAsync(group, tracker, sampling, listed, sample, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var dstReadFailed = tracker.Registration.InstanceMove && stateRead;
            if (dstReadFailed)
            {
                tracker.ReadFailed();
            }

            if (first || dstReadFailed)
            {
                await TryProbeAsync(tracker, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }
    }

    private async Task ApplySnapshotAsync(string group, GroupTracker tracker, bool sampling, bool listed, ControlSample? sample, CancellationToken cancellationToken)
    {
        var record = await reader.ReadAsync(group, cancellationToken).ConfigureAwait(false);
        // Sample all masters once an unlisted group proves it has a state.
        if (sampling && record is not null && !listed)
        {
            sample = await reader.SampleAsync(group, cancellationToken).ConfigureAwait(false);
            record = await reader.ReadAsync(group, cancellationToken).ConfigureAwait(false);
        }

        if (sampling)
        {
            tracker.Probed(sample);
        }

        tracker.Read(record);
    }

    private async Task TryProbeAsync(GroupTracker tracker, CancellationToken cancellationToken)
    {
        try
        {
            tracker.Probed(await reader.ProbeAsync(tracker.Registration.Name, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The probe shares the outage; the group stays unknown.
        }
    }

    private async Task RunAsync(GroupTracker tracker, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                TimeSpan delay;
                try
                {
                    if (await StepAsync(tracker.Registration.Name, cancellationToken).ConfigureAwait(false) is not { } next)
                    {
                        return;
                    }

                    delay = next;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A faulted step is reported and tried again; the loop of a group never ends by an error.
                    Notify(tracker.Registration.Name, "monitor_failed");
                    delay = options.RetryInterval;
                }

                await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation ends this background loop.
        }
    }

    private void RaiseAlerts(GroupTracker tracker)
    {
        var current = GroupRuleEvaluator.Evaluate(tracker.View(), options).Alerts;
        foreach (var code in current.Where(c => !tracker.Alerted.Contains(c)))
        {
            Notify(tracker.Registration.Name, code);
        }

        tracker.Alerted.Clear();
        tracker.Alerted.UnionWith(current);
    }

    // The callback belongs to the caller: its failure must not stop the group's loop, so it is swallowed here.
    private void Notify(string group, string code)
    {
        try
        {
            alert?.Invoke(group, code);
        }
        catch (Exception)
        {
            // Ignored on purpose: the alert callback belongs to the caller and its failure must not stop the loop.
        }
    }
}
