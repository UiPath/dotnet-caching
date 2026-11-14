using Microsoft.Extensions.Hosting;

namespace UiPath.Caching.Negotiation;

/// <summary>
/// Advertises this node and, where it decides what it writes, reads the node list, on timers off the read and write paths. It advertises first
/// when it starts, and writes a departing advertisement when it stops, so the nodes that outlive it count it as gone and not as unaccounted for.
/// Over Pub/Sub the order is the other way round for a subscription: a node says it is subscribed only once the server has confirmed it, and says so
/// at once, because a claim made before the subscription would hide a subscriber that says nothing. Advertisements are written one at a time, each
/// from what the node holds when its turn comes, and none after the departing one.
/// </summary>
internal sealed class FormatNegotiationService(FormatNegotiation negotiation) : IHostedService
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _advertising = new(1, 1);
    private Task[] _loops = [];
    private int _stopped;
    private int _started;
    private int _left;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!negotiation.IsActive)
        {
            // An app with no Redis connection advertises nowhere, but may still decide its binary events on the operator's word.
            if (negotiation.Negotiator is not null)
            {
                StartBlind();
            }

            return;
        }

        var options = negotiation.Options;
        if (!negotiation.HasTransport)
        {
            StartBlind();
            return;
        }

        negotiation.PubSub?.WhenConfirmed(AdvertiseConfirmed);

        using (var startup = new CancellationTokenSource(Budget, negotiation.Clock))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(startup.Token, cancellationToken))
        {
            await TickAsync(linked.Token).ConfigureAwait(false);
        }

        // A node that only reads has no decision to make, so it never reads the node list.
        _loops = negotiation.Negotiator is null
            ? [Task.Run(() => Loop(options.AdvertiseInterval, AdvertiseAsync), CancellationToken.None)]
            : [Task.Run(ScheduledLoop, CancellationToken.None)];
        Volatile.Write(ref _started, 1);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            negotiation.Logger.LogDebug(ex, "A format negotiation loop ended with an error while stopping.");
        }

        _stop.Dispose();
        if (negotiation.IsActive && negotiation.HasTransport)
        {
            try
            {
                // Behind any advertisement still being written, so a late one cannot land after the departing one and make this node look alive.
                using var timeout = new CancellationTokenSource(Budget, negotiation.Clock);
                await _advertising.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    Volatile.Write(ref _left, 1);
                    await negotiation.Store.WriteAsync(negotiation.NodeId, Departing(), timeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    _advertising.Release();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                negotiation.Logger.LogDebug(ex, "This node's departing advertisement could not be written; its advertisement expires on its own.");
            }
            finally
            {
                Volatile.Write(ref _left, 1);
            }
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        // Advertise before the node list is read, so a node that has just come back from an outage is not counted as unaccounted for by itself.
        await AdvertiseAsync(cancellationToken).ConfigureAwait(false);
        if (negotiation.Negotiator is not null)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task AdvertiseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _advertising.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _left) == 1)
                {
                    return;
                }

                // The subscriptions are read here, with the lock held, not when the service was built: what a node advertises is what it holds now, and a
                // write that was started earlier cannot land over one started later.
                var advertisement = negotiation.PubSub is { } pubSub ? negotiation.Advertisement! with { Subscriptions = pubSub.Confirmed(), PatternSubscriptions = pubSub.Patterns() } : negotiation.Advertisement!;
                await negotiation.Store.WriteAsync(negotiation.NodeId, advertisement, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _advertising.Release();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !Stopping())
        {
            negotiation.Logger.LogWarning(ex, "This node's format advertisement could not be written.");
        }
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var negotiator = negotiation.Negotiator!;
        var options = negotiation.Options;
        try
        {
            // An advertisement outlives the group of the node that wrote it, so a node that crashes is gone from the node list before it stops counting as one.
            var fresh = options.LivenessWindow + TimeSpan.FromTicks(options.AdvertiseInterval.Ticks * 2);
            var gone = TimeSpan.FromTicks(Math.Max(options.AdvertiseInterval.Ticks * 10, fresh.Ticks * 2));
            var all = await negotiation.Store.ReadAsync(cancellationToken).ConfigureAwait(false);
            var current = all.Where(a => a.Age <= fresh).ToList();
            var readers = current.Where(a => !a.Advertisement.Left).ToList();
            var streams = negotiation.StreamKeys?.Invoke() ?? [];
            var channels = negotiation.PubSub?.Channels() ?? [];
            var uncountable = negotiation.PubSub?.Uncountable() ?? [];
            var blockers = streams.Count == 0
                ? []
                : await negotiation.NodeList.BlockersAsync(streams, AgesByNode(current), cancellationToken).ConfigureAwait(false);
            var subscribers = await ReadSubscribersAsync(channels, uncountable, readers, current.Where(a => a.Advertisement.Left), cancellationToken).ConfigureAwait(false);
            negotiator.Observe(new NodeListView(readers, blockers, Complete: streams.Count > 0 || channels.Count > 0 || uncountable.Count > 0, subscribers));
            await Prune(all.Where(a => a.Age > gone).ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !Stopping())
        {
            negotiation.Logger.LogWarning(ex, "The format node list could not be read; the last decision stands until it is too old.");

            // Whatever part of the view failed, the views on either side of it are not consecutive.
            negotiation.Subscribers?.Reset();
            negotiator.ObservationFailed();
        }
    }

    private static Dictionary<string, TimeSpan> AgesByNode(IEnumerable<StoredAdvertisement> ads) =>
        ads.GroupBy(a => a.NodeId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Min(a => a.Age), StringComparer.OrdinalIgnoreCase);

    // The subscriptions the nodes that read have confirmed, summed per channel: all the live subscribers they account for. For nodes that are leaving, the
    // same sum is what they may still hold.
    private static Dictionary<string, long> Accounted(IEnumerable<StoredAdvertisement> readers)
    {
        var accounted = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var advertisement in readers.Select(r => r.Advertisement))
        {
            foreach (var (channel, count) in advertisement.Subscriptions ?? new Dictionary<string, int>())
            {
                accounted[channel] = accounted.GetValueOrDefault(channel) + count;
            }

            accounted[PubSubNodeList.PatternKey] = accounted.GetValueOrDefault(PubSubNodeList.PatternKey) + (advertisement.PatternSubscriptions ?? 0);
        }

        return accounted;
    }

    private async Task<SubscriberReading?> ReadSubscribersAsync(
        IReadOnlyList<string> channels,
        IReadOnlyList<string> uncountable,
        IEnumerable<StoredAdvertisement> readers,
        IEnumerable<StoredAdvertisement> leaving,
        CancellationToken cancellationToken)
    {
        if (negotiation.Subscribers is not { } nodeList)
        {
            return null;
        }

        if (uncountable.Count > 0)
        {
            // NUMSUB counts classic literal channels only, so a pattern or sharded one makes the whole count a guess.
            nodeList.Reset();
            return SubscriberReading.Unknown with { Why = $"the channel strategy made patterns or sharded channels, which PUBSUB NUMSUB does not count: {string.Join(", ", uncountable)}" };
        }

        if (channels.Count == 0)
        {
            nodeList.Reset();
            return null;
        }

        return await nodeList.ReadAsync(channels, Accounted(readers), Accounted(leaving), cancellationToken).ConfigureAwait(false);
    }

    // Still lists the subscriptions it holds: the connection closes a moment after the node says it is leaving, and until then the server counts it.
    private NodeAdvertisement Departing() =>
        negotiation.Advertisement! with { Left = true, Subscriptions = negotiation.PubSub?.Confirmed(), PatternSubscriptions = negotiation.PubSub?.Patterns() };

    // Off the thread that confirmed the subscription, which is the topic's, and never failing it.
    private void AdvertiseConfirmed()
    {
        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _stopped) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await AdvertiseAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                negotiation.Logger.LogDebug(ex, "The advertisement of a confirmed subscription was not written; the next one carries it.");
            }
        });
    }

    // No Streams or Pub/Sub topic, or broadcast off: nothing to read, so only the operator's word can switch, and the hold still applies.
    private void StartBlind()
    {
        if (negotiation.Negotiator is not { } negotiator)
        {
            return;
        }

        if (!negotiation.Options.AssumeAllNodesCanRead)
        {
            negotiation.Logger.LogInformation("Value and event formats stay JSON: the app has no Streams or Pub/Sub transport, so the nodes cannot be listed. Set FormatNegotiation:AssumeAllNodesCanRead to attest that they all read the desired formats.");
            return;
        }

        var blind = new NodeListView([], [], Complete: false);
        negotiator.Observe(blind);
        _loops = [Task.Run(() => Loop(negotiation.Options.ViewInterval, _ => { negotiator.Observe(blind); return Task.CompletedTask; }), CancellationToken.None)];
    }

    // Only this node stopping is quiet: a step that outran its budget is a failed step, and is logged and counted as one.
    private bool Stopping() => _stop.IsCancellationRequested;

    private async Task Prune(IReadOnlyCollection<StoredAdvertisement> gone, CancellationToken cancellationToken)
    {
        try
        {
            await negotiation.Store.PruneAsync(gone, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !Stopping())
        {
            negotiation.Logger.LogDebug(ex, "Advertisements of departed nodes could not be removed.");
        }
    }

    // The advertisement and the node list read keep their own cadences; a read that is due advertises first.
    private async Task ScheduledLoop()
    {
        var options = negotiation.Options;
        var schedule = new TickSchedule(options.AdvertiseInterval, options.ViewInterval);
        var started = negotiation.Clock.GetTimestamp();
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(schedule.Wait(negotiation.Clock.GetElapsedTime(started)), negotiation.Clock, _stop.Token).ConfigureAwait(false);
                var (advertise, refresh) = schedule.Take(negotiation.Clock.GetElapsedTime(started));
                if (refresh)
                {
                    await TickAsync(_stop.Token).ConfigureAwait(false);
                }
                else if (advertise)
                {
                    await AdvertiseAsync(_stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                negotiation.Logger.LogWarning(ex, "A format negotiation step failed; it runs again when it is next due.");
            }
        }
    }

    private async Task Loop(TimeSpan interval, Func<CancellationToken, Task> step)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval * (0.8 + (Random.Shared.NextDouble() * 0.4)), negotiation.Clock, _stop.Token).ConfigureAwait(false);
                await step(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                negotiation.Logger.LogWarning(ex, "A format negotiation step failed; it runs again at the next tick.");
            }
        }
    }
}
