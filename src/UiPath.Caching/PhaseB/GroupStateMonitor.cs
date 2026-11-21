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
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var trackers = Register(registrations);
        _loops = [.. trackers.Select(t => Task.Run(() => RunAsync(t, _stop.Token), CancellationToken.None))];
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

    public GroupRules RulesFor(string group) =>
        _groups.TryGetValue(group, out var tracker) ? GroupRuleEvaluator.Evaluate(tracker.View(), options) : GroupRules.Unaffected;

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
            var record = await reader.ReadAsync(group, cancellationToken).ConfigureAwait(false);
            if (first || record is null || tracker.View().Conflict is not null)
            {
                // A present or listed group is reconciled with a sample of every master; an unlisted absent one needs only to know whether any record exists.
                var sample = record is not null || tracker.Registration.Listed
                    ? await reader.SampleAsync(group, cancellationToken).ConfigureAwait(false)
                    : await reader.ProbeAsync(group, cancellationToken).ConfigureAwait(false);
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
            while (await StepAsync(tracker.Registration.Name, cancellationToken).ConfigureAwait(false) is { } delay)
            {
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
            alert?.Invoke(tracker.Registration.Name, code);
        }

        tracker.Alerted.Clear();
        tracker.Alerted.UnionWith(current);
    }
}
