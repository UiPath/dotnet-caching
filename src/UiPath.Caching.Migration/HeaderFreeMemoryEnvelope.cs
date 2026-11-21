namespace UiPath.Caching.Migration;

/// <summary>The header-free envelope over an envelope that also serializes to memory; the memory path is checked like the array path.</summary>
internal sealed class HeaderFreeMemoryEnvelope(IMemorySerializerProxy inner) : HeaderFreeEnvelope(inner), IMemorySerializerProxy
{
    public ReadOnlyMemory<byte> SerializeToMemory<T>(T? value)
    {
        var bytes = inner.SerializeToMemory(value);
        return StartsWithHeaderByte(bytes.Span) ? throw HeaderByte(bytes.Span[0]) : bytes;
    }
}
