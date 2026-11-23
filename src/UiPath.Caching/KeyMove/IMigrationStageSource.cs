namespace UiPath.Caching.KeyMove;

/// <summary>Supplies the stage flag of a group: 0 to 5, or null when it could not be read. A failed read must be null, never a default.</summary>
public interface IMigrationStageSource
{
    ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken);
}
