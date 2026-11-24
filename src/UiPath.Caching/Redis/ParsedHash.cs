namespace UiPath.Caching.Redis;

/// <summary>The parts of a stored hash that its read and its decode both take from it.</summary>
internal readonly record struct ParsedHash<T>(Dictionary<string, T?> Values, IDictionary<string, string?>? Metadata, bool HasEmptyMarker, long Version);
