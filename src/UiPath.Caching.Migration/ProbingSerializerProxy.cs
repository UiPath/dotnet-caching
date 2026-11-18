namespace UiPath.Caching.Migration;

internal sealed class ProbingSerializerProxy(ISerializerProxy<byte[]> inner) : ISerializerProxy<byte[]>
{
    public byte[]? Serialize(object? value)
    {
        try
        {
            return inner.Serialize(value);
        }
        catch
        {
            ReadProbe.Current?.MarkSerializeFailed();
            throw;
        }
    }

    public T? Deserialize<T>(byte[]? value)
    {
        T? result;
        try
        {
            result = inner.Deserialize<T>(value);
        }
        catch
        {
            ReadProbe.Current?.MarkUnreadable();
            throw;
        }

        if (result is null && value is { Length: > 0 })
        {
            ReadProbe.Current?.MarkUnreadable();
        }

        return result;
    }

    public bool TryDeserialize<T>(string? value, out T? result) =>
        Probe(inner.TryDeserialize(value, out result), value is { Length: > 0 });

    public bool TryDeserialize<T>(object? value, out T? result) =>
        Probe(inner.TryDeserialize(value, out result), value is not null);

    private static bool Probe(bool read, bool hadValue)
    {
        if (!read && hadValue)
        {
            ReadProbe.Current?.MarkUnreadable();
        }

        return read;
    }
}
