namespace UiPath.Caching.PhaseB;

/// <summary>Adapts an int flag getter that returns its default on a failed evaluation: it is given the sentinel -1 as that default, and -1 and every value outside 0 to 5 become null.</summary>
public sealed class IntFlagStageSource(Func<string, int, CancellationToken, ValueTask<int>> getInt) : IMigrationStageSource
{
    public const int Sentinel = -1;

    public async ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        var value = await getInt(group, Sentinel, cancellationToken).ConfigureAwait(false);
        return value is >= 0 and <= 5 ? value : null;
    }
}
