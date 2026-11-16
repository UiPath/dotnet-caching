using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UiPath.Caching.Broadcast;

/// <summary>Peer-update fields as the bytes the write held; in JSON each is base64 text escaped as a string is, so the wire is what it was when the fields were already text.</summary>
[JsonConverter(typeof(Base64Converter))]
internal sealed class PeerFieldBytes() : Dictionary<string, byte[]?>(StringComparer.Ordinal)
{
    private const int StackBytes = 256;

    private sealed class Base64Converter : JsonConverter<PeerFieldBytes>
    {
        public override PeerFieldBytes Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, PeerFieldBytes value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var (name, bytes) in value)
            {
                writer.WritePropertyName(name);
                if (bytes is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    WriteBase64(writer, bytes);
                }
            }

            writer.WriteEndObject();
        }

        private static void WriteBase64(Utf8JsonWriter writer, byte[] bytes)
        {
            var length = Base64.GetMaxEncodedToUtf8Length(bytes.Length);
            byte[]? rented = null;
            Span<byte> text = length <= StackBytes ? stackalloc byte[StackBytes] : (rented = ArrayPool<byte>.Shared.Rent(length));
            try
            {
                Base64.EncodeToUtf8(bytes, text, out _, out var written);
                writer.WriteStringValue(text[..written]);
            }
            finally
            {
                if (rented is not null)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
    }
}
