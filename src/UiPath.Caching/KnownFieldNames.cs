namespace UiPath.Caching;

internal static class KnownFieldNames
{
    /// <summary>Reserved hash field for user metadata and the cached-empty-dict marker.</summary>
    public const string MetadataKey = "_metadata_";

    public const string ExpirationKey = "_expiration_";

    /// <summary>True for any <c>_word_</c> pattern (length &gt;= 3, starts and ends with <c>'_'</c>).</summary>
    public static bool IsReserved(string field) =>
        field is not null
        && field.Length >= 3
        && field[0] == '_'
        && field[^1] == '_';

    /// <summary>True for the actual system field names (kept in sync with the per-field read validation).</summary>
    public static bool IsSystemField(string field) =>
        field == MetadataKey;
}
