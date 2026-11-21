using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>
/// The barrier on one master, on a connection of its own (WAIT blocks it): one replica or more confirms; 0, a timeout or an error re-issue the writes
/// up to <see cref="Retries"/> times with no extra backoff. A way-back transition skips at once for a master that failed within <see cref="Episode"/>.
/// </summary>
internal sealed class ServerBarrier(IServer server, TimeProvider clock)
{
    private long _failedAt = long.MinValue;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public int Retries { get; init; } = 3;

    public TimeSpan Episode { get; init; } = TimeSpan.FromMinutes(10);

    /// <param name="reissue">Sends the writes again; a repeat must rewrite what it confirms, so the next WAIT is not vacuous.</param>
    /// <param name="wayBack">A way-back transition (X6, X7 to X10, an abandon).</param>
    public async Task<BarrierOutcome> WaitAsync(Func<Task> reissue, bool wayBack)
    {
        var failedAt = Interlocked.Read(ref _failedAt);
        if (wayBack && failedAt != long.MinValue && clock.GetElapsedTime(failedAt) < Episode)
        {
            return BarrierOutcome.SkippedAfterRecentFailure;
        }

        if (await ReplicasAsync().ConfigureAwait(false) == 0)
        {
            return BarrierOutcome.NoReplica;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if ((long)await server.ExecuteAsync("WAIT", 1, (long)Timeout.TotalMilliseconds).ConfigureAwait(false) >= 1)
                {
                    return BarrierOutcome.Confirmed;
                }
            }
            catch (RedisException)
            {
                // Not confirmed, as a 0 is.
            }

            if (attempt >= Retries)
            {
                Interlocked.Exchange(ref _failedAt, clock.GetTimestamp());
                return BarrierOutcome.NotConfirmed;
            }

            await reissue().ConfigureAwait(false);
        }
    }

    private async Task<int?> ReplicasAsync()
    {
        try
        {
            return await server.RoleAsync().ConfigureAwait(false) is Role.Master master ? master.Replicas.Count : 0;
        }
        catch (RedisException)
        {
            // A server that refuses ROLE is waited on; WAIT itself tells.
            return null;
        }
    }
}
