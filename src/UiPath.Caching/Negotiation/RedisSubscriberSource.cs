using System.Net;

namespace UiPath.Caching.Negotiation;

internal sealed class RedisSubscriberSource(IRedisConnector redis) : ISubscriberSource
{
    public IReadOnlyList<EndPoint> Endpoints()
    {
        var servers = redis.Database.Multiplexer.GetServers().Where(s => s.ServerType != ServerType.Sentinel).ToList();
        var down = servers.Where(s => !s.IsConnected).Select(s => s.EndPoint.ToString()).ToList();
        if (down.Count > 0)
        {
            throw new InvalidOperationException($"The servers {string.Join(", ", down)} are not connected, so their subscribers cannot be counted.");
        }

        return [.. servers.Select(s => s.EndPoint)];
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
        var reply = (RedisResult[])(await numsub.ConfigureAwait(false))!;
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
}
