namespace UiPath.Caching.KeyMove;

/// <summary>Reads not-started, moving, drained or closed case-insensitively, or numbers zero through five, ignoring surrounding whitespace. Returns null for invalid values.</summary>
public sealed class StringFlagStageSource(Func<string, string, CancellationToken, ValueTask<string?>> getString) : IMigrationStageSource
{
    public async ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        var value = await getString(group, string.Empty, cancellationToken).ConfigureAwait(false);
        return KeyMoveStates.ParseFlag(value);
    }
}
