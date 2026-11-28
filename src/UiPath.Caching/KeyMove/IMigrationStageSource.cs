namespace UiPath.Caching.KeyMove;

/// <summary>Supplies a group's numeric stage flag, or null for a failed read; string flags use <see cref="StringFlagStageSource"/>.</summary>
public interface IMigrationStageSource
{
    ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken);
}
