using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

// Use plain INFO because Redis 6 refuses multiple section arguments.
internal sealed class GenerationReader(Func<RecordSide, IConnectionMultiplexer> multiplexer) : IGenerationSource
{
    public async Task<ServerGeneration?> ReadAsync(RecordSide side, CancellationToken cancellationToken)
    {
        try
        {
            var servers = multiplexer(side).GetEndPoints().Select(e => multiplexer(side).GetServer(e)).Where(static s => !s.IsReplica).ToArray();
            if (servers.Length == 0)
            {
                return null;
            }

            var runIds = new List<string>(servers.Length);
            var replicationIds = new List<string>(servers.Length);
            foreach (var server in servers.OrderBy(static s => s.EndPoint.ToString(), StringComparer.Ordinal))
            {
                if (!server.IsConnected)
                {
                    return null;
                }

                var text = (string?)await server.ExecuteAsync("INFO").WaitAsync(cancellationToken).ConfigureAwait(false);
                if (ConnectionProof.InfoField(text, "role:") != "master"
                    || ConnectionProof.InfoField(text, "run_id:") is not { Length: > 0 } runId
                    || ConnectionProof.InfoField(text, "master_replid:") is not { Length: > 0 } replicationId)
                {
                    return null;
                }

                runIds.Add(runId);
                replicationIds.Add(replicationId);
            }

            return new ServerGeneration(string.Join(',', runIds), string.Join(',', replicationIds));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return null;
        }
    }
}
