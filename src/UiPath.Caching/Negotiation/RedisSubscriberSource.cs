using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Negotiation;

internal sealed class RedisSubscriberSource(IRedisConnector redis, ILogger? logger = null) : ISubscriberSource
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(50);

    private readonly SemaphoreSlim _checking = new(1, 1);
    private NumsubCheck? _verdict;
    private bool _countedMoreBefore;

    /// <summary>How long the check waits for its own subscription to show in the count.</summary>
    internal TimeSpan CheckWait { get; init; } = TimeSpan.FromSeconds(1);

    public IReadOnlyList<EndPoint> Endpoints()
    {
        var servers = ClusterServers.Nodes(redis.Database.Multiplexer.GetServers().Where(s => s.ServerType != ServerType.Sentinel)).ToList();
        var down = servers.Where(s => !s.IsConnected).Select(s => s.EndPoint.ToString()).ToList();
        if (down.Count > 0)
        {
            throw new InvalidOperationException($"The servers {string.Join(", ", down)} are not connected, so their subscribers cannot be counted.");
        }

        return [.. servers.Select(s => s.EndPoint)];
    }

    /// <summary>
    /// Subscribes to a channel nobody else knows and sums <c>PUBSUB NUMSUB</c> for it over every server: a count of clients says one. More than one is a server
    /// that answers for others, as Azure Managed Redis does. A reliable or an unreliable verdict is kept for the life of the source, an inconclusive one is asked again.
    /// More than one latches only when the check before it, one that came to a count, said so too: after a dropped connection StackExchange.Redis can restore one
    /// subscription on two cluster nodes, and a healthy cluster then counts two once.
    /// </summary>
    public async Task<NumsubCheck> CheckCountsAsync(CancellationToken cancellationToken)
    {
        if (_verdict is { } known)
        {
            return known;
        }

        await _checking.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_verdict is { } decided)
            {
                return decided;
            }

            var verdict = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (verdict == NumsubCheck.NotClientCounts && !_countedMoreBefore)
            {
                _countedMoreBefore = true;
                return NumsubCheck.Inconclusive;
            }

            if (verdict != NumsubCheck.Inconclusive)
            {
                _verdict = verdict;
                if (verdict == NumsubCheck.NotClientCounts)
                {
                    logger?.LogWarning("The server's PUBSUB NUMSUB does not count clients (a proxy such as Azure Managed Redis), so Pub/Sub subscribers cannot be counted and writes stay on the current format.");
                }
            }

            return verdict;
        }
        finally
        {
            _checking.Release();
        }
    }

    public async Task<ServerCount?> CountAsync(EndPoint endpoint, IReadOnlyList<string> channels, CancellationToken cancellationToken)
    {
        await redis.ConnectAsync(cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        var multiplexer = redis.Database.Multiplexer;
        var server = multiplexer.GetServer(endpoint);
        var prefix = ChannelPrefix(multiplexer);
        List<object> subscribers = ["NUMSUB", .. channels.Select(channel => prefix + channel)];
        var numsub = server.ExecuteAsync("PUBSUB", subscribers);
        var numpat = server.ExecuteAsync("PUBSUB", new List<object> { "NUMPAT" });
        try
        {
            await Task.WhenAll(numsub, numpat).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return null;
        }

        // Name, count, name, count: in the order asked.
        var reply = ((RedisResult[]?)(await numsub.ConfigureAwait(false)))!;
        return new([.. Enumerable.Range(0, channels.Count).Select(i => (long)reply[(2 * i) + 1])], (long)(await numpat.ConfigureAwait(false)));
    }

    /// <summary>A server that will not run the command, by permission, by a renamed or disabled command, or because it does not know it.</summary>
    internal static bool IsRefusal(Exception ex) =>
        ex is RedisCommandException
        || (ex is RedisServerException server
            && (server.Message.StartsWith("NOPERM", StringComparison.OrdinalIgnoreCase)
                || server.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                || server.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase)));

    // The multiplexer puts its channel prefix in front of every channel it subscribes to or publishes on, and the server only knows the result.
    // The multiplexer's configuration string carries it, and a multiplexer that does not say has none.
    private static string ChannelPrefix(IConnectionMultiplexer multiplexer) =>
        multiplexer.Configuration is { Length: > 0 } configuration ? (string?)ConfigurationOptions.Parse(configuration).ChannelPrefix ?? string.Empty : string.Empty;

    private static void Ignore(RedisChannel channel, RedisValue value)
    {
        // The probe subscribes only to be counted; what arrives on its channel is of no use.
    }

    private async Task<NumsubCheck> ProbeAsync(CancellationToken cancellationToken)
    {
        await redis.ConnectAsync(cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        var endpoints = Endpoints();
        var name = "caching-probe-" + Guid.NewGuid().ToString("N");
        var channel = RedisChannel.Literal(name);
        var subscriber = redis.Database.Multiplexer.GetSubscriber();
        Action<RedisChannel, RedisValue> ignore = Ignore;
        await subscriber.SubscribeAsync(channel, ignore).ConfigureAwait(false);
        try
        {
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                var replies = await Task.WhenAll(endpoints.Select(endpoint => CountAsync(endpoint, [name], cancellationToken))).ConfigureAwait(false);
                if (replies.Any(reply => reply is null))
                {
                    // A refusal is the count's to report, and it does so on every read.
                    return NumsubCheck.Reliable;
                }

                var sum = replies.Sum(reply => reply!.Channels[0]);
                if (sum > 1)
                {
                    return NumsubCheck.NotClientCounts;
                }

                if (sum == 1)
                {
                    return NumsubCheck.Reliable;
                }

                if (deadline.Elapsed >= CheckWait)
                {
                    return NumsubCheck.Inconclusive;
                }

                await Task.Delay(PollEvery, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await subscriber.UnsubscribeAsync(channel, ignore).ConfigureAwait(false);
        }
    }
}
