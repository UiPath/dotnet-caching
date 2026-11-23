namespace UiPath.Caching.Redis;

/// <summary>What a fill does with a tombstone: write over it, leave it because it is younger than the generator, or leave it because its age is not known.</summary>
internal enum TombstoneVerdict
{
    Replace,
    Young,
    Unknown,
}
