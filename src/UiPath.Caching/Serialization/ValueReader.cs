using System.Buffers;

namespace UiPath.Caching.Serialization;

/// <summary>Reads a stored value by its format header: headerless bytes go to the JSON proxy, headered ones to the registered serializer of that id.</summary>
internal sealed class ValueReader(ISerializerProxy<byte[]> json, ValueSerializerRegistry registry)
{
    public const string UnreadableMetric = "cache.serializer.unreadable";

    // The one proxy whose output is known to be JSON; a subclass or another proxy may store bytes that start with a header byte.
    private readonly bool _jsonProxyIsDefault = json.GetType() == typeof(SystemJsonByteSerializerProxy);

    // Only a policy that writes a non-JSON format puts headers where a custom proxy could not tell them from its own bytes; Read lists never open the gate.
    /// <summary>False for a custom proxy or a subclass: its bytes are not known to be JSON, so a write it makes cannot stand in for one with a header.</summary>
    public bool JsonProxyIsDefault => _jsonProxyIsDefault;

    public bool SniffsFor(CachePolicy? policy) =>
        !registry.IsEmpty && (_jsonProxyIsDefault || WritesHeaders(policy));

    public ReadOutcome<T> Read<T>(byte[] value)
    {
        if (!ValueHeader.TryRead(value, out var formatId, out var headerLength))
        {
            try
            {
                return ReadOutcome<T>.Read(json.Deserialize<T>(value));
            }
            catch (OutOfMemoryException ex)
            {
                // Headerless bytes read as they do with no registry, where OutOfMemoryException is a logged miss.
                return ReadOutcome<T>.Failed(ex);
            }
        }

        if (formatId < 0 || !registry.TryGetByFormat(formatId, out var serializer))
        {
            return ReadOutcome<T>.Unreadable(formatId);
        }

        try
        {
            return ReadOutcome<T>.Read(serializer.Deserialize<T>(formatId, new ReadOnlySequence<byte>(value, headerLength, value.Length - headerLength)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A miss that keeps the key: a version of T another node writes, or a disposed serializer at shutdown, says nothing about the data.
            return ReadOutcome<T>.Failed(ex);
        }
    }

    private static bool WritesHeaders(CachePolicy? policy) =>
        policy is not null
        && (policy.WriteSerializer is not null
            || (policy.Serializer?.Write is { } name && !string.Equals(name, CacheValueFormats.JsonName, StringComparison.OrdinalIgnoreCase)));
}
