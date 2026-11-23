namespace UiPath.Caching.Migration;

internal sealed partial class RoutedCache<T>
{
    /// <summary>True when this call made the key; the source refuses it, and a create not sent or unknown throws.</summary>
    internal async ValueTask<bool> TryAddAsync(string key, T? value, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var admission = Admit(out var route);
        var deadline = Deadline(expiry);
        TryAddResult<T> result = default;

        // The create has no other client to fall back on, so a create that was not sent is the answer.
        var outcome = await MutateAsync(
            key,
            route with { NoFallback = true },
            async (client, token) =>
            {
                result = await client.TryAddAsync(key, value, deadline, token).ConfigureAwait(false);
                return result.Kind switch
                {
                    TryAddKind.Created => WriteOutcome.Committed,
                    TryAddKind.Exists => WriteOutcome.NotSent,
                    _ => result.Failure,
                };
            },
            deadline ?? DateTimeOffset.MaxValue,
            null,
            fill: null,
            cancellationToken).ConfigureAwait(false);

        return result.Kind switch
        {
            TryAddKind.Created => true,
            TryAddKind.Exists => false,
            _ => throw new CacheUnavailableException($"A conditional create on cache '{_registration.Name}' {(outcome == WriteOutcome.Unknown ? "has an unknown outcome" : "was not sent")}."),
        };
    }

    /// <summary>A refresh is a write: evicted here and on peers, pending if uncertain; the source refuses it.</summary>
    internal async ValueTask<bool> RefreshAsync(string key, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var admission = Admit(out var route);
        var resolved = Resolve(expiry);
        var outcome = await MutateAsync(key, route with { NoFallback = true }, (client, token) => client.RefreshAsync(key, resolved, token), null, null, fill: null, cancellationToken).ConfigureAwait(false);
        return outcome == WriteOutcome.Committed;
    }
}
