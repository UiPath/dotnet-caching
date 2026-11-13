namespace UiPath.Caching;

/// <summary>Which value serializer a cache policy writes with and reads. Merged field by field against the default policy, a <c>null</c> field inheriting.</summary>
public sealed class SerializerPolicy
{
    /// <summary>A registered serializer name, or <c>"json"</c>, never blank; <c>null</c> inherits (default JSON).</summary>
    public string? Write { get; init; }

    /// <summary>Names of the formats this policy reads, never empty and including <c>Write</c>; <c>null</c> inherits (default every registered format).</summary>
    /// <remarks>
    /// A value in another format is a counted miss that keeps the key. With <c>FormatNegotiation.Mode</c> at <c>Auto</c> the list must include <c>"json"</c>, since the writer falls back to JSON, except with no Redis connection or a custom <c>ISerializerProxy&lt;byte[]&gt;</c>.
    /// Merging two policies in code replaces the list as a whole. Layered configuration does not: the binder merges arrays by index, so an override of
    /// <c>["json"]</c> over a base <c>["a", "b"]</c> binds as <c>["json", "b"]</c>, and an empty array binds as <c>null</c> (inherit). To override safely, set every index
    /// the base layer sets, or declare <c>Read</c> in one layer only.
    /// </remarks>
    public IReadOnlyList<string>? Read { get; init; }

    /// <summary>Rewrite a value read in another format into the write format; <c>null</c> inherits (default off).</summary>
    public bool? MigrateOnRead { get; init; }
}
