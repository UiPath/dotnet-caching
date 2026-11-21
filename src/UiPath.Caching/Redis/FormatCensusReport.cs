namespace UiPath.Caching.Redis;

/// <summary>What a census of the strings and hashes under a key pattern found, by format and by reason.</summary>
/// <remarks>
/// <see cref="ByFormat"/> is what <see cref="RedisCacheProvider.CountFormatsAsync(string, CancellationToken)"/> returns. <see cref="ByReason"/> says why a value is where it is,
/// and one key can have several reasons: <c>format_json</c>, <c>format_&lt;name&gt;</c> for a registered serializer, <c>format_unknown</c>, <c>empty</c> (the cached-null marker),
/// <c>oversize</c> (a headerless string over 256 KiB, which a rewrite does not carry), <c>no_ttl</c> (a string with no lifetime, which only a write removes), and
/// <c>hash_sampled</c> (a hash, of which only the first 256 user fields are looked at). A value equal to <c>default(T)</c> or one that would not come out the same
/// needs the type, so only <see cref="RedisCacheProvider.SweepFormatsAsync{T}"/> reports those.
/// </remarks>
public sealed class FormatCensusReport(IReadOnlyDictionary<string, long> byFormat, IReadOnlyDictionary<string, long> byReason, long scanned)
{
    /// <summary>The keys counted under each format: <c>json</c>, a registered serializer's name, or <see cref="CacheValueFormats.UnknownName"/>.</summary>
    public IReadOnlyDictionary<string, long> ByFormat { get; } = byFormat;

    /// <summary>The keys counted under each reason; a reason with no key is absent.</summary>
    public IReadOnlyDictionary<string, long> ByReason { get; } = byReason;

    /// <summary>How many keys were looked at; a key that went away during the scan is not counted.</summary>
    public long Scanned { get; } = scanned;
}
