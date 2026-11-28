namespace UiPath.Caching.KeyMove;

/// <summary>Reads off, copying, copied, done, or zero through five, ignoring case and surrounding whitespace. Returns null for invalid values.</summary>
public sealed class StringFlagStageSource(Func<string, string, CancellationToken, ValueTask<string?>> getString) : IMigrationStageSource
{
    public async ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        var value = await getString(group, string.Empty, cancellationToken).ConfigureAwait(false);
        return KeyMoveStates.ParseFlag(value);
    }
}
