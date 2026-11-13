namespace UiPath.Caching.Redis;

/// <summary>Marks a miss that left a field the call could not read in the hash, which a write that only sets fields would leave there.</summary>
internal interface IUnreadableEntry
{
}
