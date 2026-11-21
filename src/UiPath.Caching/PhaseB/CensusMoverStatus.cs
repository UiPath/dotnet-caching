using System.Collections.Concurrent;
using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>What the leader reads from the census: the residue and the canary, with D1 passing only on a finished mover pass.</summary>
/// <remarks>The census behind the drain report is reused for <see cref="ReuseFor"/>, since a scan of every master on each 2 second step would load the servers; a failed census is no evidence.</remarks>
/// <param name="moverFinishedPass">Whether the mover's own pass for the group finished after the acknowledgement of S3; null when there is no mover.</param>
internal sealed class CensusMoverStatus(MasterCensus census, TimeProvider time, Func<string, string, CancellationToken, Task<bool>>? moverFinishedPass = null) : IMoverStatus
{
    public static readonly TimeSpan ReuseFor = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (long At, CensusResult? Result)> _recent = new(StringComparer.Ordinal);

    public async Task<DrainReport?> DrainAsync(string group, string direction, CancellationToken cancellationToken)
    {
        // The cheap check first: the mover's own answer costs no scan.
        var moverPassed = moverFinishedPass is not null && await moverFinishedPass(group, direction, cancellationToken).ConfigureAwait(false);
        CensusResult? result;
        try
        {
            result = await RecentAsync(group, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return null;
        }

        if (result is null)
        {
            return null;
        }

        // Going forward the canary needs S3's time; without it the census is no evidence. In reverse the canary only counts.
        if (direction != "rev" && result.Canary is null)
        {
            return null;
        }

        return new DrainReport(moverPassed && result.Movable == 0, result.Residue, result.Canary ?? 0);
    }

    public async Task<IReadOnlyDictionary<string, long>> ResidueCensusAsync(string group, CancellationToken cancellationToken) =>
        (await census.RunAsync(group, cancellationToken).ConfigureAwait(false))?.Residue ?? new Dictionary<string, long>();

    private async Task<CensusResult?> RecentAsync(string group, CancellationToken cancellationToken)
    {
        if (_recent.TryGetValue(group, out var held) && time.GetElapsedTime(held.At) < ReuseFor)
        {
            return held.Result;
        }

        var at = time.GetTimestamp();
        var fresh = await census.RunAsync(group, cancellationToken).ConfigureAwait(false);
        _recent[group] = (at, fresh);
        return fresh;
    }
}
