using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UiPath.Caching.Broadcast;

/// <summary>Peer-update fields still in base64; the first read of any value decodes them all, and a bad entry throws <see cref="FormatException"/> there.</summary>
[JsonConverter(typeof(EncodedConverter))]
internal sealed class LazyPeerFields(IDictionary<string, string?> encoded) : IReadOnlyDictionary<string, byte[]?>
{
    private readonly Lazy<Dictionary<string, byte[]?>> _decoded = new(() => Decode(encoded));

    public int Count => encoded.Count;

    public IEnumerable<string> Keys => encoded.Keys;

    public IEnumerable<byte[]?> Values => _decoded.Value.Values;

    private IDictionary<string, string?> Encoded => encoded;

    public byte[]? this[string key] => _decoded.Value[key];

    public bool ContainsKey(string key) => encoded.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out byte[]? value) => _decoded.Value.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, byte[]?>> GetEnumerator() => _decoded.Value.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static Dictionary<string, byte[]?> Decode(IDictionary<string, string?> encoded)
    {
        var decoded = new Dictionary<string, byte[]?>(encoded.Count);
        foreach (var (name, text) in encoded)
        {
            decoded[name] = text is null ? null : Convert.FromBase64String(text);
        }

        return decoded;
    }

    private sealed class EncodedConverter : JsonConverter<LazyPeerFields>
    {
        public override LazyPeerFields Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, LazyPeerFields value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Encoded, JsonSerializerOptions.Default);
    }
}
