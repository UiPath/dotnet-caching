using Microsoft.Extensions.Logging;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration;

/// <summary>Runs the pin of a node and answers the format gate; nothing here throws to a caller.</summary>
internal sealed partial class PinCoordinator(RouterCore core, Func<string, bool> pinWhen, IPinStore? store, string? scope, TimeSpan refreshInterval, TimeSpan maxViewAge, ILogger logger) : IFormatWriteGate, IAdvertisementTokens
{
    private const string NotWanted = "format_not_wanted";
    private const string NoPin = "no_pin";
    private const string UnpinnedGroup = "unpinned_group";
    private const string NodeListUnreadable = "node_list_unreadable";
    private const string StateNotWritten = "state_not_written";
    private const string AckNotRecorded = "ack_not_recorded";
    private const string AckChanged = "ack_changed";

    private static readonly char[] ForbiddenInGroup = ['+', '@', '=', '{', '}'];

    private readonly Dictionary<string, GroupPin> _byName = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedPredicate = [];
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly string _incarnation = Guid.NewGuid().ToString("N");
    private volatile GroupPin[] _groups = [];
    private volatile GateSnapshot? _snapshot;
    private int _wanted;
    private int _refreshRequested;

    /// <summary>Raised when an acknowledgement or a switch changed, so the entry is written at once.</summary>
    public event Action? Changed;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The per-group write format setting; with it the gate covers the groups that want MessagePack, without it every group, as soon as the gate is asked.</summary>
    public Func<string, string?>? WriteFormat { get; init; }

    public string? Scope { get; } = scope;

    /// <summary>The field of this node's router entry; the gate needs it in the node list.</summary>
    public string OwnField { get; set; } = string.Empty;

    /// <summary>How long a node's entry counts as live.</summary>
    public TimeSpan Fresh { get; set; } = TimeSpan.FromSeconds(150);

    /// <summary>How long a successful read of the switch state lets a group that is not pinned take the source path.</summary>
    public TimeSpan StateValidity => TimeSpan.FromTicks(refreshInterval.Ticks * 3);

    public IReadOnlyList<GroupPin> Groups => _groups;

    public GroupPin GroupFor(string group)
    {
        if (group.IndexOfAny(ForbiddenInGroup) >= 0)
        {
            throw new InvalidOperationException($"Group '{group}' cannot be pinned: a pinned group name holds none of + @ = {{ }}.");
        }

        lock (_byName)
        {
            if (!_byName.TryGetValue(group, out var pin))
            {
                _byName[group] = pin = new GroupPin(group);
                _groups = [.. _byName.Values];
            }

            return pin;
        }
    }

    /// <summary>The tokens this node advertises: <c>pin=g</c> for an acknowledged group and <c>sw=g</c> for one it knows is switched.</summary>
    public void AddTo(List<string> tokens)
    {
        foreach (var group in _groups)
        {
            if (group.Acked)
            {
                tokens.Add(RouterAdvertisement.Pin + group.Group);
            }

            if (group.Switched)
            {
                tokens.Add(RouterAdvertisement.SwitchedPrefix + group.Group);
            }
        }
    }

    /// <summary>Null when MessagePack writes may go ahead. Asking is what makes the pin a switch: the state is written only once a format write is wanted.</summary>
    /// <remarks>With a write format setting, the groups that asked for MessagePack, or are writing it, are the ones that count; otherwise every group does, from the first ask.</remarks>
    public string? Hold() => HoldFor(null);

    /// <summary><see cref="Hold"/>, and the writing group must itself be covered.</summary>
    /// <remarks>The group about to write must be covered by the snapshot, acknowledged and durable. Checked for the writing group directly, so a concurrent answer that clears its wanted bit cannot drop it from the check.</remarks>
    public string? HoldFor(GroupPin? writing)
    {
        if (WriteFormat is null && Volatile.Read(ref _wanted) == 0 && Interlocked.Exchange(ref _wanted, 1) == 0)
        {
            RequestRefresh();
        }

        var snapshot = _snapshot;
        if (snapshot is null)
        {
            return "no_view";
        }

        if (snapshot.Reason is not null)
        {
            return snapshot.Reason;
        }

        if (core.Clock.GetElapsedTime(snapshot.Stamp) > maxViewAge)
        {
            return "view_stale";
        }

        // Every group that wants it, not the ones that were pinned when the gate opened: one that lost its pin since closes it.
        var wanted = Wanted(_groups);
        if (wanted.Length == 0)
        {
            return NotWanted;
        }

        foreach (var group in wanted)
        {
            if (!snapshot.Covers(group.Group))
            {
                return "group_not_evaluated";
            }

            if (!group.Acked || !group.Durable)
            {
                return "pin_lost";
            }
        }

        if (writing is not null)
        {
            if (!snapshot.Covers(writing.Group))
            {
                return "group_not_evaluated";
            }

            if (!writing.Acked || !writing.Durable)
            {
                return "pin_lost";
            }
        }

        return null;
    }

    /// <summary>Records what the group's write format setting asked for on this call; a group that newly asks starts a pass at once.</summary>
    public void Want(GroupPin group, bool wanted)
    {
        if (group.SetWanted(wanted) && wanted)
        {
            RequestRefresh();
        }
    }

    public void RequestRefresh() => Volatile.Write(ref _refreshRequested, 1);

    /// <summary>Reads the setting and the state once before the first call is admitted, for at most <paramref name="bound"/> of real time; a group that is not known by then fails closed.</summary>
    public void Initialize(TimeSpan bound)
    {
        EvaluatePins();
        // Not disposed until the read ends, so a read that outlives the wait is still cut off at the bound.
        var limit = new CancellationTokenSource(bound);
        var read = Task.Run(() => RefreshAsync(limit.Token), CancellationToken.None);
        _ = read.ContinueWith(_ => limit.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            read.Wait(bound + TimeSpan.FromSeconds(1), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogInitializeFailed(ex);
        }
    }

    public void Start() => core.Run(RunAsync);

    /// <summary>Applies the setting to every group; false when it changed anything. A setting that throws leaves the group as it was.</summary>
    public bool EvaluatePins()
    {
        var changed = false;
        foreach (var group in _groups)
        {
            bool pinned;
            try
            {
                pinned = pinWhen(group.Group);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (_failedPredicate.Add(group.Group))
                {
                    LogPredicateFailed(ex, group.Group);
                }

                changed |= group.UpdateAck();
                continue;
            }

            _failedPredicate.Remove(group.Group);
            changed |= group.SetPinned(pinned);
            ClearWanted(group);
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }

    /// <summary>One pass over the switch state, the node list and the gate; false when something could not be read, which the next pass retries.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        // One pass at a time: acknowledgements reach the store in the order they were made.
        await _refreshing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nodes = new NodeCache(store, cancellationToken, LogNodesFailed);
            var healthy = true;
            var groups = _groups;
            foreach (var group in groups)
            {
                healthy &= await RefreshGroupAsync(group, nodes, cancellationToken).ConfigureAwait(false);
            }

            healthy &= await EvaluateGateAsync(groups, nodes, cancellationToken).ConfigureAwait(false);
            return healthy;
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private static bool IsAcknowledgement(string record)
    {
        var separator = record.LastIndexOf(":", StringComparison.Ordinal);
        return separator > 0 && long.TryParse(record.AsSpan(separator + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var generation) && generation % 2 == 1;
    }

    // A group whose setting went back to JSON or null stops counting at once, without waiting for a write; asking again is what makes it count, so this never starts a switch.
    private void ClearWanted(GroupPin group)
    {
        if (WriteFormat is not { } format || !group.Wanted)
        {
            return;
        }

        try
        {
            if (!WriteFormats.WantsMessagePack(format(group.Group)))
            {
                group.SetWanted(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The write path sends the envelope for an answer that throws and clears the bit, so this does the same.
            _ = ex;
            group.SetWanted(false);
        }
    }

    private GroupPin[] Wanted(GroupPin[] groups) =>
        WriteFormat is null
            ? (Volatile.Read(ref _wanted) == 1 ? groups : [])
            : [.. groups.Where(group => group.Wanted || group.Writing)];

    private async Task<bool> RefreshGroupAsync(GroupPin group, NodeCache nodes, CancellationToken cancellationToken)
    {
        // The withdrawal this read can confirm: one that happened after it began would not be covered by what it finds.
        var withdrawals = group.Withdrawals;
        var generation = group.AckGeneration;
        SwitchRead read;
        try
        {
            var step = await ReadOrPublishAsync(group, generation, cancellationToken).ConfigureAwait(false);
            if (!step.Applied)
            {
                LogAckSuperseded(group.Group);
                return false;
            }

            read = step.State;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            LogStateReadFailed(ex, group.Group);
            return false;
        }

        switch (read)
        {
            case SwitchRead.Unreadable:
                // Neither switched nor not: the group stays unknown, and fails closed once the last good read is too old.
                LogStateUnreadable(group.Group);
                return false;
            case SwitchRead.Switched:
                Notify(group.LatchSwitched(durable: true));
                break;
            default:
                if (!await ResolveMissingAsync(group, nodes, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                group.ConfirmUnswitched(withdrawals, generation);
                break;
        }

        group.StateRead(core.Clock);
        return true;
    }

    // Every change of the acknowledgement goes to the store before the state is read with it, so a withdrawal and the read that confirms it are one step there.
    private async Task<AckPublish> ReadOrPublishAsync(GroupPin group, long generation, CancellationToken cancellationToken)
    {
        var pinStore = store ?? throw new InvalidOperationException("The router has no connection to keep the pin state on.");
        if (generation == 0 || (generation % 2 == 0 && group.PublishedGeneration == generation))
        {
            return new AckPublish(true, await pinStore.ReadStateAsync(group.Group, cancellationToken).ConfigureAwait(false));
        }

        if (OwnField.Length == 0)
        {
            throw new InvalidOperationException("The router has no node field to record its acknowledgement under.");
        }

        var published = await pinStore.PublishAckAsync(group.Group, OwnField, _incarnation, generation, cancellationToken).ConfigureAwait(false);
        if (published.Applied)
        {
            group.PublishedGeneration = generation;
        }

        return published;
    }

    // No key: never switched, or lost. A node that saw the switch, or a peer that reports it, restores the key; with neither, the group is not switched.
    private async Task<bool> ResolveMissingAsync(GroupPin group, NodeCache nodes, CancellationToken cancellationToken)
    {
        if (!group.Switched)
        {
            var list = await nodes.GetAsync().ConfigureAwait(false);
            if (list is null)
            {
                return false;
            }

            if (!PinGateEvaluator.SwitchedBy(list, Scope, group.Group, Fresh))
            {
                return true;
            }

            Notify(group.LatchSwitched(durable: false));
            LogSwitchFromPeers(group.Group);
        }
        else
        {
            group.SetDurable(false);
            LogStateLost(group.Group);
        }

        return await WriteStateAsync(group, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WriteStateAsync(GroupPin group, CancellationToken cancellationToken)
    {
        try
        {
            await store!.RestoreSwitchAsync(group.Group, cancellationToken).ConfigureAwait(false);
            group.SetDurable(true);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            LogStateWriteFailed(ex, group.Group);
            return false;
        }
    }

    private async Task<bool> EvaluateGateAsync(GroupPin[] groups, NodeCache nodes, CancellationToken cancellationToken)
    {
        var wanted = Wanted(groups);
        var reason = await GateReasonAsync(wanted, nodes, cancellationToken).ConfigureAwait(false);
        if (reason == NodeListUnreadable && _snapshot is not null)
        {
            // As the format negotiation does: the last view stands until it is too old.
            return false;
        }

        var snapshot = new GateSnapshot(reason, core.Clock.GetTimestamp(), [.. wanted.Select(group => group.Group)]);
        var wasOpen = _snapshot is { Reason: null };
        _snapshot = snapshot;
        if (wasOpen != (reason is null))
        {
            LogGate(reason ?? "open");
        }

        return reason is not (NodeListUnreadable or StateNotWritten or AckNotRecorded or AckChanged);
    }

    // The gate is one for the whole stack while the choice is per group: it opens only when every group that wants MessagePack is pinned, acknowledged and switched, so one closed group holds them all and a group that does not want it never does.
    private async Task<string?> GateReasonAsync(GroupPin[] pinned, NodeCache nodes, CancellationToken cancellationToken)
    {
        if (pinned.Length == 0)
        {
            return WriteFormat is null && Volatile.Read(ref _wanted) == 1 ? NoPin : NotWanted;
        }

        if (pinned.Any(group => !group.Applied))
        {
            return UnpinnedGroup;
        }

        if (pinned.Any(group => !group.Acked))
        {
            return "unacknowledged";
        }

        var list = await nodes.GetAsync().ConfigureAwait(false);
        if (list is null)
        {
            return NodeListUnreadable;
        }

        var verdict = PinGateEvaluator.Evaluate(list, Scope, OwnField, [.. pinned.Select(group => group.Group)], Fresh);
        if (verdict.Blocker is not null)
        {
            return verdict.Blocker;
        }

        if (pinned.Any(group => !verdict.Satisfied.Contains(group.Group)))
        {
            return "node_without_pin";
        }

        // The point of no return is recorded before any write can use it, and only against the acknowledgements as they are in the store at that moment.
        foreach (var group in pinned.Where(group => !group.Durable))
        {
            var refused = await CommitAsync(group, verdict.Required, cancellationToken).ConfigureAwait(false);
            if (refused is not null)
            {
                return refused;
            }
        }

        return null;
    }

    // Not latched until the store has taken it: a commit that is refused, or whose answer never came, is validated again when it runs, so a node that withdrew meanwhile is not bound by it, and a later read finds it if it did run.
    private async Task<string?> CommitAsync(GroupPin group, IReadOnlyList<string> required, CancellationToken cancellationToken)
    {
        try
        {
            var records = await store!.ReadAcksAsync(group.Group, cancellationToken).ConfigureAwait(false);
            var expected = new List<KeyValuePair<string, string>>(required.Count);
            foreach (var field in required)
            {
                if (!records.TryGetValue(field, out var record) || !IsAcknowledgement(record))
                {
                    return AckNotRecorded;
                }

                expected.Add(new(field, record));
            }

            if (!await store.CommitSwitchAsync(group.Group, expected, cancellationToken).ConfigureAwait(false))
            {
                return AckChanged;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            LogStateWriteFailed(ex, group.Group);
            return StateNotWritten;
        }

        Notify(group.LatchSwitched(durable: true));
        return null;
    }


    private void Notify(bool changed)
    {
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var last = core.Clock.GetTimestamp();
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, core.Clock, cancellationToken).ConfigureAwait(false);
                if (EvaluatePins())
                {
                    RequestRefresh();
                }

                var wait = failures == 0 ? refreshInterval : RouterAdvertiser.Backoff(failures, refreshInterval);
                if (Interlocked.Exchange(ref _refreshRequested, 0) == 1 || core.Clock.GetElapsedTime(last) >= wait)
                {
                    last = core.Clock.GetTimestamp();
                    failures = await RefreshAsync(cancellationToken).ConfigureAwait(false) ? 0 : failures + 1;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures++;
                LogLoopFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The pin setting for group {Group} threw; the group keeps its previous pin.")]
    private partial void LogPredicateFailed(Exception exception, string group);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The switch state of group {Group} could not be read; the group fails closed once its last good read is too old.")]
    private partial void LogStateReadFailed(Exception exception, string group);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The switch state of group {Group} holds a value this version does not know; the group fails closed once its last good read is too old.")]
    private partial void LogStateUnreadable(string group);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The switch state of group {Group} is gone from Redis; this node, which saw the group switched, writes it again.")]
    private partial void LogStateLost(string group);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The switch state of group {Group} is missing from Redis, but a node reports the group switched; it is treated as switched and written again.")]
    private partial void LogSwitchFromPeers(string group);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The switch state of group {Group} could not be written; MessagePack writes stay held.")]
    private partial void LogStateWriteFailed(Exception exception, string group);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The store holds a later acknowledgement of this node for group {Group}; the group stays unconfirmed until the next pass.")]
    private partial void LogAckSuperseded(string group);

    [LoggerMessage(Level = LogLevel.Information, Message = "The format gate is {State}.")]
    private partial void LogGate(string state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The first read of the pin state failed or was late; groups that are not known fail closed until a read succeeds.")]
    private partial void LogInitializeFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A pin step failed; it runs again.")]
    private partial void LogLoopFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The node list could not be read for the pin; the gate stays closed and the state of an absent key cannot be told from a lost one.")]
    private partial void LogNodesFailed(Exception exception);

    private sealed record GateSnapshot(string? Reason, long Stamp, string[] Groups)
    {
        public bool Covers(string group) => Array.IndexOf(Groups, group) >= 0;
    }

    /// <summary>One read of the node hash per pass, shared by the groups; null when it failed.</summary>
    private sealed class NodeCache(IPinStore? store, CancellationToken cancellationToken, Action<Exception> onFailure)
    {
        private IReadOnlyList<NodeEntry>? _nodes;
        private bool _failed;

        public async Task<IReadOnlyList<NodeEntry>?> GetAsync()
        {
            if (_nodes is not null || _failed)
            {
                return _nodes;
            }

            try
            {
                _nodes = await (store ?? throw new InvalidOperationException("The router has no connection to read the node list on.")).ReadNodesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
            {
                _failed = true;
                onFailure(ex);
            }

            return _nodes;
        }
    }
}
