namespace UiPath.Caching.Migration;

/// <summary>What a hash codec makes of the stored fields of one side.</summary>
/// <param name="Class">Ok for a hash with a user field; Null for one with none (an empty hash, kept as the empty marker while nulls are cached); TooLarge above the field cap, unread, or above the byte cap; Unreadable when a field or the metadata cannot be read.</param>
/// <param name="Fields">The user fields; a field stored without a value reads as null. Empty unless the class is Ok or Null, or the hash is above only the byte cap.</param>
/// <param name="Metadata">The user metadata, or null.</param>
/// <param name="Version">The write count the hash holds, or 0.</param>
/// <param name="Length">The number of stored fields, system fields included: the hash's <c>HLEN</c>.</param>
/// <param name="Bytes">The field names and values together, in bytes; 0 when the hash was not read.</param>
internal readonly record struct DecodedHash<T>(DecodeClass Class, IReadOnlyDictionary<string, T?> Fields, IDictionary<string, string?>? Metadata, long Version, int Length, long Bytes)
    where T : class
{
    public static DecodedHash<T> Of(DecodeClass decodeClass, int length, long bytes = 0) =>
        new(decodeClass, new Dictionary<string, T?>(), null, 0, length, bytes);
}
