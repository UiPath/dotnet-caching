namespace UiPath.Caching.Serialization;

/// <summary>Writes a value that was read in one format in another, only if it comes out the same.</summary>
internal sealed class ValueRewriter(ISerializerProxy<byte[]> json, ValueSerializerRegistry registry)
{
    // A subclass or another proxy may store bytes that are not JSON, so they cannot be compared as JSON.
    private readonly bool _jsonProxyIsDefault = json.GetType() == typeof(SystemJsonByteSerializerProxy);
    private readonly ValueReader _reader = new(json, registry);

    /// <summary>Whether a headerless value can be checked at all; with a custom JSON proxy none can.</summary>
    public bool ComparesJson => _jsonProxyIsDefault;

    /// <summary>
    /// False, with an empty <paramref name="payload"/>, for a value that is not one to rewrite or that would change; otherwise the value in the target's format,
    /// which the caller disposes after the write has completed. The value is read from <paramref name="original"/> here, so the caller's own object is never touched,
    /// and the payload is read back and checked too, so a target that cannot carry the type is found out.
    /// </summary>
    /// <remarks>Throws what the target serializer throws.</remarks>
    public bool TryReencode<T>(byte[] original, ICacheValueSerializer target, out SerializedPayload payload)
    {
        payload = default;
        if (original.Length == 0 || !TryRead<T>(original, out var value) || IsDefault(value) || !IsLossless(original, value!, target))
        {
            return false;
        }

        var written = SerializedPayload.Serialize(target, registry.FormatIdsOf(target).Span, value!);
        // The payload is the caller's only once it is handed over; until then a check that throws gives its array back.
        try
        {
            if (!TryRead<T>(written.Memory.ToArray(), out var back) || IsDefault(back) || !IsLossless(original, back!, target))
            {
                written.Dispose();
                return false;
            }
        }
        catch
        {
            written.Dispose();
            throw;
        }

        payload = written;
        return true;
    }

    private bool TryRead<T>(byte[] bytes, out T? value)
    {
        try
        {
            var outcome = _reader.Read<T>(bytes);
            value = outcome.IsMiss ? default : outcome.Value;
            return !outcome.IsMiss;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Bytes that read once and no longer do say nothing the rewrite can keep.
            value = default;
            return false;
        }
    }

    private bool IsLossless<T>(byte[] original, T value, ICacheValueSerializer target)
    {
        if (!ValueHeader.TryRead(original, out var formatId, out _))
        {
            return _jsonProxyIsDefault && LosslessCheck.Json(original, json.Serialize(value)!);
        }

        if (!registry.TryGetByFormat(formatId, out var source) || ReferenceEquals(source, target))
        {
            return false;
        }

        using var again = SerializedPayload.Serialize(source, registry.FormatIdsOf(source).Span, value);
        return LosslessCheck.Headered(original, again.Memory.Span);
    }
}
