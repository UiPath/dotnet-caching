using System.Net;

namespace UiPath.Caching.Negotiation;

/// <summary>
/// Finds the subscribers of a Pub/Sub channel that no advertisement accounts for. A subscriber is local to the server it connected to, so the live count
/// is the sum of <c>PUBSUB NUMSUB</c> over every server, one round trip per server for all the channels and for <c>PUBSUB NUMPAT</c>, which counts the
/// pattern subscriptions NUMSUB leaves out; what advertisements account for is the sum of the subscriptions they list, patterns included, and a node that is leaving may or may not still hold its own. More than that is a node that advertises nothing. Fewer is the
/// opposite: a node that went away, whose claim outlives it. A count that does not add up either way counts against a format only once two views running
/// show it on the same channel, so a node that has subscribed and not yet advertised, or one that is resubscribing, is absorbed, and a count counts for an
/// upgrade only once two views running show every channel clean over the same number of servers.
/// </summary>
internal sealed class PubSubNodeList(ISubscriberSource source)
{
    /// <summary>
    /// Stands for the pattern subscriptions of the servers in the counts and the claims. <c>PUBSUB NUMPAT</c> counts the whole server, not the channels of this
    /// node, so another app that subscribes to a pattern is in it too.
    /// </summary>
    public const string PatternKey = "(patterns)";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _confirmedShort = new(StringComparer.Ordinal);
    private Dictionary<string, ChannelState>? _previous;
    private int _endpoints;

    public Task<SubscriberReading> ReadAsync(IReadOnlyCollection<string> channels, IReadOnlyDictionary<string, long> accounted, CancellationToken cancellationToken) =>
        ReadAsync(channels, accounted, new Dictionary<string, long>(), cancellationToken);

    /// <param name="channels">The channels this node listens on.</param>
    /// <param name="accounted">The subscriptions the nodes that read have confirmed, per channel.</param>
    /// <param name="leaving">The subscriptions nodes that are leaving claimed, per channel: each may or may not still be held, until its advertisement expires.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<SubscriberReading> ReadAsync(IReadOnlyCollection<string> channels, IReadOnlyDictionary<string, long> accounted, IReadOnlyDictionary<string, long> leaving, CancellationToken cancellationToken)
    {
        try
        {
            var endpoints = source.Endpoints();
            if (endpoints.Count == 0)
            {
                Reset();
                return SubscriberReading.Unknown with { Why = "no server is connected" };
            }

            IReadOnlyList<string> asked = [.. channels];
            var replies = await Task.WhenAll(endpoints.Select(endpoint => source.CountAsync(endpoint, asked, cancellationToken))).ConfigureAwait(false);
            if (replies.Any(reply => reply is null))
            {
                // One server that cannot say makes the sum a guess, and a guess is not an answer.
                Reset();
                return SubscriberReading.Unknown with { Why = "a server refused PUBSUB NUMSUB" };
            }

            return Judge(asked, [.. replies.Select(reply => reply!)], endpoints.Count, accounted, leaving);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Reset();
            throw;
        }
    }

    /// <summary>Two views are consecutive only if nothing came between them: a view that failed, was partial or could not count starts the debounce again, but not a deficit already confirmed.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _previous = null;
        }
    }

    private SubscriberReading Judge(IReadOnlyList<string> asked, List<ServerCount> replies, int endpoints, IReadOnlyDictionary<string, long> accounted, IReadOnlyDictionary<string, long> leaving)
    {
        // The patterns are one more thing to account for, with no deficit: NUMPAT counts the unique patterns, so claims may exceed it.
        IReadOnlyList<string> channels = [.. asked, PatternKey];
        var surplus = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            var previous = _previous;
            var next = new Dictionary<string, ChannelState>(channels.Count, StringComparer.Ordinal);
            bool unaccounted = false, confirmedShort = false, replaced = false;
            var settled = previous is not null && _endpoints == endpoints;
            for (var i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                var live = i < asked.Count ? replies.Sum(reply => reply.Channels[i]) : replies.Sum(reply => reply.Patterns);
                var claimed = accounted.GetValueOrDefault(channel);
                var over = live > claimed + leaving.GetValueOrDefault(channel);
                var under = i < asked.Count && live < claimed;
                ChannelState? before = previous is not null && previous.TryGetValue(channel, out var found) ? found : null;
                if (over)
                {
                    surplus[channel] = live - claimed - leaving.GetValueOrDefault(channel);
                }

                if (under)
                {
                    missing.Add(channel);
                }

                var shortConfirmed = under && before is { Under: true };
                unaccounted |= over && before is { Over: true };
                confirmedShort |= shortConfirmed;
                if (_confirmedShort.TryGetValue(channel, out var shortClaim) && !under)
                {
                    replaced |= claimed >= shortClaim;
                    _confirmedShort.Remove(channel);
                }

                if (shortConfirmed)
                {
                    _confirmedShort[channel] = claimed;
                }

                settled &= !over && !under && before is { Over: false, Under: false };
                next[channel] = new(over, under);
            }

            _previous = next;
            _endpoints = endpoints;
            return new(
                Known: true,
                Unaccounted: unaccounted,
                Settled: settled,
                surplus.Values.Sum(),
                [.. surplus.Keys],
                Short: confirmedShort,
                ShortChannels: [.. missing],
                Replaced: replaced);
        }
    }

    private readonly record struct ChannelState(bool Over, bool Under);
}
