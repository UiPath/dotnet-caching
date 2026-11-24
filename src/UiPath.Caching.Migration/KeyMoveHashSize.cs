namespace UiPath.Caching.Migration;

/// <summary>The one size rule of a hash, shared by its read, its promotion and the inventory so that they cannot disagree: the field count decides, and a hash read adds the byte check.</summary>
internal static class KeyMoveHashSize
{
    /// <summary>Above the cap the hash is too large without being read; the length is its field count, which is its <c>HLEN</c>.</summary>
    public static DecodeClass Of(long fieldCount, int maxHashFields) =>
        fieldCount > maxHashFields ? DecodeClass.TooLarge : DecodeClass.Ok;

    /// <summary>What a hash of this many fields is to the inventory, which never reads it: an empty one cannot be stored, and one above the cap is too large.</summary>
    public static DecodeClass OfLength(long fieldCount, int maxHashFields) =>
        fieldCount == 0 ? DecodeClass.Null : Of(fieldCount, maxHashFields);

    /// <summary>Whether the field names and values of a hash read together exceed what a value may be.</summary>
    public static bool ExceedsBytes(long bytes, int maxValueBytes) => bytes > maxValueBytes;
}
