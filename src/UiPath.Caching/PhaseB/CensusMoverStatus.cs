namespace UiPath.Caching.PhaseB;

/// <summary>What the leader reads from the census: the residue and the canary. D1 also needs a finished mover pass, which only the mover can report, so without one the criterion never passes.</summary>
/// <param name="moverFinishedPass">Whether the mover's own pass for the group finished after the acknowledgement of S3; null when there is no mover.</param>
internal sealed class CensusMoverStatus(MasterCensus census, Func<string, string, CancellationToken, Task<bool>>? moverFinishedPass = null) : IMoverStatus
{
    public async Task<DrainReport?> DrainAsync(string group, string direction, CancellationToken cancellationToken)
    {
        if (await census.RunAsync(group, cancellationToken).ConfigureAwait(false) is not { } result)
        {
            return null;
        }

        // Going forward the canary needs S3's time; without it the census is no evidence. In reverse the canary only counts.
        if (direction != "rev" && result.Canary is null)
        {
            return null;
        }

        var passed = moverFinishedPass is not null && await moverFinishedPass(group, direction, cancellationToken).ConfigureAwait(false) && result.Movable == 0;
        return new DrainReport(passed, result.Residue, result.Canary ?? 0);
    }

    public async Task<IReadOnlyDictionary<string, long>> ResidueCensusAsync(string group, CancellationToken cancellationToken) =>
        (await census.RunAsync(group, cancellationToken).ConfigureAwait(false))?.Residue ?? new Dictionary<string, long>();
}
