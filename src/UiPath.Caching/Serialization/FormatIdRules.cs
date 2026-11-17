namespace UiPath.Caching.Serialization;

/// <summary>The value format id rules, shared by the builder, which throws argument exceptions, and the registry, which reports options failures.</summary>
internal static class FormatIdRules
{
    public static bool IsAllowed(int id) =>
        id is >= 0x01 and <= 0x0F and not 0x09 and not 0x0A and not 0x0D
            or >= CacheValueFormats.FirstConsumer and <= CacheValueFormats.LastConsumer
            or >= CacheValueFormats.FirstExtended and <= CacheValueFormats.LastExtended;

    public static bool IsEventId(int id) =>
        id is >= 0x01 and <= 0x0F and not 0x09 and not 0x0A and not 0x0D
            or >= CacheValueFormats.FirstConsumer and <= CacheValueFormats.LastConsumer;

    public static string Hex(int id) => $"0x{id:X2}";

    /// <summary>The first rule <paramref name="ids"/> break; <paramref name="claimedBy"/> names the serializer that already owns an id, or null.</summary>
    public static Fault? Find(string name, IReadOnlyList<int> ids, Func<int, string?> claimedBy)
    {
        if (ids.Count == 0)
        {
            return new(false, $"Value serializer '{name}' declares no format ids.");
        }

        var extended = ids[0] >= CacheValueFormats.FirstExtended;
        var seen = new HashSet<int>();
        foreach (var id in ids)
        {
            if (!IsAllowed(id))
            {
                return new(
                    true,
                    $"Value serializer '{name}' declares format id {Hex(id)}, which is not one of 0x01-0x0F (except 0x09, 0x0A, 0x0D), 0x10-0x1E or 0x100-0x1FF.");
            }

            if ((id >= CacheValueFormats.FirstExtended) != extended)
            {
                return new(false, $"Value serializer '{name}' mixes one-byte and extended format ids; all of its ids need the same header length.");
            }

            if (!seen.Add(id))
            {
                return new(false, $"Format id {Hex(id)} is listed twice for value serializer '{name}'.");
            }

            if (claimedBy(id) is { } owner)
            {
                return new(false, $"Format id {Hex(id)} is already claimed by value serializer '{owner}'; '{name}' cannot claim it.");
            }
        }

        return null;
    }

    public readonly record struct Fault(bool OutOfRange, string Message);
}
