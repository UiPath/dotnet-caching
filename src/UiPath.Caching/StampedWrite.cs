namespace UiPath.Caching;

/// <summary>What a write guarded by a stamp did.</summary>
internal enum StampedWrite
{
    Failed = 0,
    Won,
    Lost,
}
