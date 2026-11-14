using MessagePack;
using MessagePack.Formatters;

namespace UiPath.Caching.MessagePack;

// Hands already serialized MessagePack to the serializer's compression, so a large value is not serialized twice.
internal sealed class RawMessagePackResolver : IFormatterResolver
{
    public static readonly RawMessagePackResolver Instance = new();

    public IMessagePackFormatter<T>? GetFormatter<T>() =>
        typeof(T) == typeof(RawMessagePack) ? (IMessagePackFormatter<T>)(object)RawFormatter.Instance : null;

    internal sealed class RawFormatter : IMessagePackFormatter<RawMessagePack>
    {
        public static readonly RawFormatter Instance = new();

        // WriteRaw(span) copies through 4 KiB scratch segments, which LZ4 would compress as separate blocks.
        public void Serialize(ref MessagePackWriter writer, RawMessagePack value, MessagePackSerializerOptions options)
        {
            var length = value.Bytes.Length;
            value.Bytes.Span.CopyTo(writer.GetSpan(length));
            writer.Advance(length);
        }

        public RawMessagePack Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
            throw new NotSupportedException();
    }
}
