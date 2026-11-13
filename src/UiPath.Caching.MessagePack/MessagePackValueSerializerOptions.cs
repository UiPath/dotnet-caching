using MessagePack;

namespace UiPath.Caching.MessagePack;

public sealed class MessagePackValueSerializerOptions
{
    /// <summary>A value whose MessagePack bytes reach this size is LZ4-compressed when that makes it smaller; zero compresses every value, <see cref="int.MaxValue"/> none.</summary>
    public int CompressionThresholdBytes { get; set; } = 1024;

    /// <summary>A compressed value that claims more than this many bytes uncompressed is rejected on read; the default is 64 MiB. A claim above 255 times the compressed size is rejected whatever this is. Deserializing can allocate several times the decompressed size (up to about 8x for typed collections, more for an <c>object[]</c> of nils), so size this to available memory. It replaces <see cref="MessagePackSecurity.MaximumDecompressedSize"/> on a supplied <see cref="SerializerOptions"/>.</summary>
    public int MaxDecompressedBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>Defaults to the contractless resolver with <see cref="MessagePackSecurity.UntrustedData"/>, so a value that is not marked for MessagePack still serializes and hash-based collections use collision-resistant comparers, falling back to the default comparer for a key type MessagePack has none for (<see cref="TimeSpan"/>, <see cref="decimal"/>, tuples, custom structs). A supplied instance that carries <see cref="MessagePackSecurity.TrustedData"/> is switched to the default security; pass your own <see cref="MessagePackSecurity"/> to opt out. Any compression set on it is ignored: the serializer compresses by <see cref="CompressionThresholdBytes"/>.</summary>
    public MessagePackSerializerOptions? SerializerOptions { get; set; }
}
