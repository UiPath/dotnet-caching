namespace UiPath.Caching.Redis;

internal sealed class ReservedRedisKeyspace : IReservedRedisKeyspace
{
    public ReservedRedisKeyspace(string keyspace, string owner, bool isDistributedCache = false)
    {
        Keyspace = SingleSegment(Guard.NotNullOrWhiteSpace(keyspace, nameof(keyspace)));
        Owner = Guard.NotNullOrWhiteSpace(owner, nameof(owner));
        IsDistributedCache = isDistributedCache;
    }

    public string Keyspace { get; }

    public string Owner { get; }

    /// <summary>Set only by <c>AddDistributedCache</c>: <see cref="Owner"/> is display text a caller could imitate.</summary>
    public bool IsDistributedCache { get; }

    /// <summary>
    /// A keyspace fills one segment between <c>AppShortName</c> and the rest of the key. Letters and digits
    /// only, so it cannot span segments under any punctuation separator: <c>"x"</c> and <c>"x:y"</c> would
    /// otherwise both be reservable while a key of <c>"y:k"</c> under the first renders what <c>"k"</c>
    /// renders under the second. <c>CacheOptions.Separator</c> may itself be alphanumeric, which this rule
    /// cannot anticipate, so the pair is checked against the configured separator when the cache resolves.
    /// </summary>
    private static string SingleSegment(string keyspace) =>
        keyspace.All(char.IsLetterOrDigit)
            ? keyspace
            : throw new ArgumentException(
                $"Redis keyspace '{keyspace}' must be a single segment of letters and digits. It fills the slot between AppShortName and the rest of the key, so anything else risks overlapping another keyspace.",
                nameof(keyspace));
}
