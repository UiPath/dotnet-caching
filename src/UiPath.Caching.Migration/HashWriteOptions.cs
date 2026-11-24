namespace UiPath.Caching.Migration;

/// <summary>How a hash write lands: a replace of the key or a merge of the fields, until when, and with which user metadata.</summary>
/// <param name="Mode">Replace the key or merge the fields into it.</param>
/// <param name="Deadline">When the hash expires; null for no expiry.</param>
/// <param name="Metadata">The user metadata of the hash, which only the native side stores.</param>
internal readonly record struct HashWriteOptions(HashCacheSetOption Mode, DateTimeOffset? Deadline, IDictionary<string, string?>? Metadata = null);
