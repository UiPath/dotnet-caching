namespace UiPath.Caching.KeyMove;

/// <summary>Reads case-insensitive stage names or numbers from zero through five, returning null for missing or unknown values.</summary>
public sealed class StringFlagStageSource(Func<string, string, CancellationToken, ValueTask<string?>> getString) : IMigrationStageSource
{
    public async ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        var value = await getString(group, string.Empty, cancellationToken).ConfigureAwait(false);
        return KeyMoveStates.ParseFlag(value);
    }
}
