using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>The lease <c>mg:{g}:lead</c>: taken when free, renewed by its holder; a holder that cannot renew stops writing once the time it could rely on has run out.</summary>
internal sealed class LeaderLease(MoveScripts scripts, RedisKey key, KeyMoveStateOptions options, TimeProvider time)
{
    private long _sentAt;
    private int _held;

    public string Token { get; } = MoveScripts.NewNonce();

    public LeaseFence Fence => new(key, Token);

    /// <summary>True while the last successful take or renewal, counted from before it was sent, is younger than the lease lifetime.</summary>
    public bool HoldsLease => Volatile.Read(ref _held) == 1 && time.GetElapsedTime(Volatile.Read(ref _sentAt)) < options.LeaseTtl;

    /// <summary>One take or renewal; a failed call changes nothing, so the old validity runs out by itself.</summary>
    public async Task<bool> TryHoldAsync()
    {
        var sent = time.GetTimestamp();
        var code = await scripts.LeaseAsync(key, Token, (long)options.LeaseTtl.TotalMilliseconds).ConfigureAwait(false);
        if (code == MoveScripts.LeaseHeldByAnother)
        {
            Volatile.Write(ref _held, 0);
            return false;
        }

        Volatile.Write(ref _sentAt, sent);
        Volatile.Write(ref _held, 1);
        return true;
    }

    public async Task ReleaseAsync()
    {
        Volatile.Write(ref _held, 0);
        await scripts.LeaseReleaseAsync(key, Token).ConfigureAwait(false);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    await TryHoldAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is RedisException or TimeoutException)
                {
                    // The lease stays valid only for what the last renewal bought.
                }

                await Task.Delay(options.LeaseRenewInterval, time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
    }
}
