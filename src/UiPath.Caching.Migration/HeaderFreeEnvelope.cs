namespace UiPath.Caching.Migration;

/// <summary>The router's envelope, marked header-free; a write whose first byte would read as a format header fails.</summary>
/// <remarks>
/// The envelope plays the role of JSON for the in-place keys: a value starting with a header byte would be read by a registered serializer, or kept as
/// unreadable, instead of reaching the envelope. The check mirrors the library's header rule: 0x01 to 0x1F except tab, line feed and carriage return.
/// </remarks>
internal class HeaderFreeEnvelope(ISerializerProxy<byte[]> inner) : ISerializerProxy<byte[]>, IHeaderFreeSerializerProxy
{
    /// <summary>The envelope wrapped once, keeping its memory path; a proxy marked by someone else is wrapped too, so the guard always runs.</summary>
    public static ISerializerProxy<byte[]> Wrap(ISerializerProxy<byte[]> envelope) => envelope switch
    {
        HeaderFreeEnvelope => envelope,
        IMemorySerializerProxy memory => new HeaderFreeMemoryEnvelope(memory),
        _ => new HeaderFreeEnvelope(envelope),
    };

    public static bool StartsWithHeaderByte(ReadOnlySpan<byte> bytes) => !bytes.IsEmpty && bytes[0] is >= 0x01 and <= 0x1F and not 0x09 and not 0x0A and not 0x0D;

    public byte[]? Serialize(object? value)
    {
        var bytes = inner.Serialize(value);
        return StartsWithHeaderByte(bytes) ? throw HeaderByte(bytes![0]) : bytes;
    }

    public T? Deserialize<T>(byte[]? value) => inner.Deserialize<T>(value);

    public bool TryDeserialize<T>(string? value, out T? result) => inner.TryDeserialize(value, out result);

    public bool TryDeserialize<T>(object? value, out T? result) => inner.TryDeserialize(value, out result);

    protected static InvalidOperationException HeaderByte(byte first) =>
        new($"The envelope wrote a value starting with byte 0x{first:X2}, which a reader takes as a format header; the envelope must never start a value with 0x01 to 0x1F other than tab, line feed or carriage return.");
}
