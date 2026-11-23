using UiPath.Caching.Negotiation;

namespace UiPath.Caching.Broadcast.Redis;

/// <summary>Whose consumer groups belong to nodes that left, judged from one read of the node list per maintainer pass.</summary>
/// <remarks>A group is named for its node, so the owner is the entry of the same name. Anything that cannot be judged is kept.</remarks>
internal sealed class DepartedOwners
{
    private readonly Dictionary<string, StoredAdvertisement> _byNode;
    private readonly bool _anyFresh;
    private readonly bool _sameClock;
    private readonly TimeSpan _fresh;
    private readonly TimeSpan _gone;
    private readonly TimeSpan _idle;

    internal DepartedOwners(IReadOnlyList<StoredAdvertisement> entries, FormatNegotiationOptions negotiation, TimeSpan quarantine, bool sameClock = true)
    {
        _sameClock = sameClock;
        _byNode = new Dictionary<string, StoredAdvertisement>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            _byNode[entry.NodeId] = entry;
        }

        (_fresh, _gone) = Windows(negotiation);
        _idle = IdleHorizon(negotiation, quarantine);
        _anyFresh = entries.Any(e => !e.Advertisement.Left && e.Age <= _fresh);
    }

    internal enum Verdict
    {
        Live,

        /// <summary>No entry, or one between fresh and gone; kept.</summary>
        Unknown,

        Left,

        Stale,

        /// <summary>No entry, in a fleet that advertises, and every consumer idle past the horizon.</summary>
        AbsentIdle,
    }

    /// <summary>Some node advertises.</summary>
    /// <remarks>Without that an absent owner cannot be judged, so nothing more is read for it.</remarks>
    public bool AnyFresh => _anyFresh;

    /// <summary>Null when the node list could not be read: then nothing is judged departed.</summary>
    public static async Task<DepartedOwners?> ReadAsync(NodeListStore store, FormatNegotiationOptions negotiation, TimeSpan quarantine, CancellationToken cancellationToken)
    {
        try
        {
            var entries = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new DepartedOwners(entries, negotiation, quarantine, sameClock: !store.ScriptsRefused);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>How the node list counts an advertisement: fresh within the liveness window plus two intervals, gone once it outlived twice that.</summary>
    public static (TimeSpan Fresh, TimeSpan Gone) Windows(FormatNegotiationOptions negotiation)
    {
        var fresh = negotiation.LivenessWindow + TimeSpan.FromTicks(negotiation.AdvertiseInterval.Ticks * 2);
        return (fresh, TimeSpan.FromTicks(Math.Max(negotiation.AdvertiseInterval.Ticks * 10, fresh.Ticks * 2)));
    }

    /// <summary>The idle time every consumer of an absent owner's group must exceed: the gone window or the quarantine, whichever is longer.</summary>
    public static TimeSpan IdleHorizon(FormatNegotiationOptions negotiation, TimeSpan quarantine)
    {
        var gone = Windows(negotiation).Gone;
        return gone > quarantine ? gone : quarantine;
    }

    /// <summary>The verdict from the node list alone.</summary>
    /// <remarks>An absent owner needs <see cref="JudgeAbsent"/>.</remarks>
    public Verdict Judge(string group)
    {
        if (!_byNode.TryGetValue(group, out var entry))
        {
            return Verdict.Unknown;
        }

        if (entry.Advertisement.Left)
        {
            return Verdict.Left;
        }

        // An age measured against another server's clock could call a live node stale, so without the script no entry is stale.
        if (entry.Age > _gone)
        {
            return _sameClock ? Verdict.Stale : Verdict.Unknown;
        }

        return entry.Age <= _fresh ? Verdict.Live : Verdict.Unknown;
    }

    public bool IsAbsent(string group) => !_byNode.ContainsKey(group);

    /// <summary>The verdict for an owner with no entry.</summary>
    /// <remarks>
    /// Departed only when the fleet advertises, the server's idle time counts read attempts (Redis 7.2 and later), and every consumer of the group has
    /// been idle past the horizon. A live consumer polls, so its idle time stays short even on a quiet stream.
    /// </remarks>
    public Verdict JudgeAbsent(IReadOnlyCollection<StreamConsumerInfo> consumers, bool idleCountsAttempts) =>
        _anyFresh && idleCountsAttempts && consumers.Count > 0 && consumers.All(c => c.IdleTimeInMilliseconds > _idle.TotalMilliseconds)
            ? Verdict.AbsentIdle
            : Verdict.Unknown;
}
