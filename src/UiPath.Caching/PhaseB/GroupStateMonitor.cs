using System.Collections.Concurrent;

namespace UiPath.Caching.PhaseB;

/// <summary>
/// The snapshot loop of a node: reads the state of every registered group at startup (retrying until the answer is definitive), keeps relevant groups polled,
/// and answers <see cref="RulesFor"/> from memory, so no hot path reads Redis.
/// </summary>
/// <param name="alert">Told once each time a group's alert becomes active.</param>
internal sealed class GroupStateMonitor(IStateReader reader, PhaseBStateOptions options, TimeProvider time, Action<string, string>? alert = null) : IAckedIdentitySource
{
    private readonly ConcurrentDictionary<string, GroupTracker> _groups = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;
    private Task[] _loops = [];

    /// <summary>Registers the groups and starts one loop for each; the first reads run in the background.</summary>
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
                // A group polled slowly (closed, or not listed and idle) is as old as its interval allows, which G7 must not read as a stale node.
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

    /// <summary>Records that the node drained every unit of the older identities and now runs under this one.</summary>
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

    /// <summary>One read of one group: the new state, the probe where it is needed, and the time to wait before the next read; null when the group needs no more polling.</summary>
    internal async Task<TimeSpan?> StepAsync(string group, CancellationToken cancellationToken)
    {
        var tracker = _groups[group];
        var first = tracker.View().Answer == StateAnswer.Unknown;
        try
        {
            // The records are read before the state: a transition that lands between the two reads makes the state newer than the records, never older.
            // A listed group is reconciled with a sample of every master; an unlisted one is only probed, and sampled once a state shows it is a Phase B group.
            var before = tracker.View();
            var sampling = first || before.Answer != StateAnswer.Present || before.Conflict is not null;
            var listed = tracker.Registration.Listed;
            var sample = sampling
                ? (listed ? await reader.SampleAsync(group, cancellationToken).ConfigureAwait(false) : await reader.ProbeAsync(group, cancellationToken).ConfigureAwait(false))
                : null;
            var record = await reader.ReadAsync(group, cancellationToken).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (first)
            {
                await TryProbeAsync(tracker, cancellationToken).ConfigureAwait(false);
            }

            RaiseAlerts(tracker);
            return options.RetryInterval;
        }

        RaiseAlerts(tracker);
        var view = tracker.View();
        return GroupRuleEvaluator.IsRelevant(view) ? GroupRuleEvaluator.PollInterval(view, options) : (tracker.Registration.Flagged ? options.SlowPollInterval : null);
    }

    /// <summary>Registers groups without starting their loops; <see cref="StepAsync"/> drives them.</summary>
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

    // While the state is unreadable a record that is still readable shows the group is closed, or that it exists at all.
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
            // Stopped.
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
            // Reporting is best effort.
        }
    }
}
