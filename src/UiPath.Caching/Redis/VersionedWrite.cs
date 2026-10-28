namespace UiPath.Caching.Redis;

/// <param name="Version">The write count the write reached; zero when the inner tier did not count it.</param>
/// <param name="Fields">The written fields as the bytes the inner tier holds, in base64; null unless the write is one a peer can apply.</param>
internal readonly record struct VersionedWrite(bool Written, long Version, IDictionary<string, string?>? Fields);
