namespace UiPath.Caching.Redis;

internal static class KeyEscaping
{
    // The composed name goes through the configured strategy, which under ShardKeyEnabled refuses a name whose
    // braces form no tag -- so a single stream named 'app:st:{}topic' would abort the pass for every other
    // stream. The quarantine hash has no need of the stream's tag, the two being deleted one command at a time,
    // so the braces are escaped away. The whole name, not just the stream key: CacheOptions.Separator is only
    // required to be non-whitespace, so the marker can carry braces too. Escaping '%' first keeps this injective.
    public static string EscapeBraces(string name) =>
        name
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("{", "%7B", StringComparison.Ordinal)
            .Replace("}", "%7D", StringComparison.Ordinal);
}
