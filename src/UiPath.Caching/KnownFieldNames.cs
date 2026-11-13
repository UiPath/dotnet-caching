namespace UiPath.Caching;

internal static class KnownFieldNames
{
    /// <summary>Reserved hash field for user metadata and the cached-empty-dict marker.</summary>
    public const string MetadataKey = "_metadata_";

    public const string ExpirationKey = "_expiration_";

    /// <summary>The hash field that counts a hash's writes, and the event property that names the count a write reached.</summary>
    public const string VersionKey = "_version_";

    /// <summary>Peer-update event property: the fields a write changed, each as the bytes Redis holds, in base64.</summary>
    /// <remarks>The peer-update properties are read only by a node that applies the update; any other node drops its copy, as it would for a plain set.</remarks>
    public const string PeerFieldsKey = "_peerFields_";

    /// <summary>Peer-update event property: the write's expiry. Not <see cref="ExpirationKey"/>, which a node acts on by keeping its copy.</summary>
    public const string PeerExpirationKey = "_peerExpiration_";

    /// <summary>Peer-update event property: the write's metadata. Not <see cref="MetadataKey"/>, which a node acts on by keeping its copy.</summary>
    public const string PeerMetadataKey = "_peerMetadata_";

    /// <summary>The key a topic-wide event carries: it names no entry.</summary>
    public const string AllKeys = "*";

    /// <summary>True for any <c>_word_</c> pattern (length &gt;= 3, starts and ends with <c>'_'</c>).</summary>
    public static bool IsReserved(string field) =>
        field is not null
        && field.Length >= 3
        && field[0] == '_'
        && field[^1] == '_';

    /// <summary>True for the actual system field names (kept in sync with the per-field read validation).</summary>
    public static bool IsSystemField(string field) =>
        field == MetadataKey || field == VersionKey;

    public static bool IsSystemField(ReadOnlySpan<byte> field) =>
        IsMetadata(field) || IsVersion(field);

    /// <summary>Compares the name's bytes in place, whatever the storage of the value Redis handed back, without making a string of it.</summary>
    public static bool IsSystemField(RedisValue name) =>
        IsMetadata(name) || IsVersion(name);

    public static bool IsMetadata(ReadOnlySpan<byte> field) =>
        field.SequenceEqual("_metadata_"u8);

    public static bool IsMetadata(RedisValue name) =>
        SameBytes(name, "_metadata_"u8);

    public static bool IsVersion(ReadOnlySpan<byte> field) =>
        field.SequenceEqual("_version_"u8);

    public static bool IsVersion(RedisValue name) =>
        SameBytes(name, "_version_"u8);

    private static bool SameBytes(RedisValue name, ReadOnlySpan<byte> expected)
    {
        if (name.GetByteCount() != expected.Length)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[expected.Length];
        name.CopyTo(bytes);
        return bytes.SequenceEqual(expected);
    }
}
