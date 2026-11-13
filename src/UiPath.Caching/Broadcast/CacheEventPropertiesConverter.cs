using System.Text.Json;
using System.Text.Json.Serialization;

namespace UiPath.Caching.Broadcast;

/// <summary>Reads the known event properties as the types <see cref="ChangeToken{T}"/> uses; a key or shape it does not know stays a <see cref="JsonElement"/>.</summary>
internal sealed class CacheEventPropertiesConverter : JsonConverter<IDictionary<string, object?>>
{
    // The names of KnownFieldNames: this file is also compiled into the CloudEvents assembly, which cannot see that class.
    private const string VersionKey = "_version_";
    private const string ExpirationKey = "_expiration_";
    private const string PeerExpirationKey = "_peerExpiration_";
    private const string MetadataKey = "_metadata_";
    private const string PeerMetadataKey = "_peerMetadata_";
    private const string PeerFieldsKey = "_peerFields_";

    private delegate bool ReadMember<TValue>(ref Utf8JsonReader reader, out TValue? member);

    public override IDictionary<string, object?>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException();
        }

        var properties = new Dictionary<string, object?>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString()!;
            reader.Read();
            properties[key] = ReadValue(ref reader, key);
        }

        return properties;
    }

    public override void Write(Utf8JsonWriter writer, IDictionary<string, object?> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, JsonSerializerOptions.Default);

    private static object? ReadValue(ref Utf8JsonReader reader, string key)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        // A shape that does not fit is read again, whole, as a JsonElement: the token's fallback decides what it is.
        var probe = reader;
        if (TryReadTyped(ref probe, key, out var typed))
        {
            reader = probe;
            return typed;
        }

        return JsonSerializer.Deserialize<JsonElement>(ref reader, JsonSerializerOptions.Default);
    }

    private static bool TryReadTyped(ref Utf8JsonReader reader, string key, out object? value)
    {
        value = null;
        switch (key)
        {
            case VersionKey when reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var version):
                value = version;
                return true;
            case ExpirationKey or PeerExpirationKey when reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var expiration):
                value = expiration;
                return true;
            case MetadataKey or PeerMetadataKey when reader.TokenType == JsonTokenType.StartObject:
                return TryReadMap<string>(ref reader, TryReadText, out value);
            case PeerFieldsKey when reader.TokenType == JsonTokenType.StartObject:
                if (!TryReadMap<string>(ref reader, TryReadText, out value))
                {
                    return false;
                }

                value = new LazyPeerFields((Dictionary<string, string?>)value!);
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadMap<TValue>(ref Utf8JsonReader reader, ReadMember<TValue> readMember, out object? value)
    {
        var map = new Dictionary<string, TValue?>();
        value = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                value = map;
                return true;
            }

            var name = reader.GetString()!;
            reader.Read();
            if (!readMember(ref reader, out var member))
            {
                return false;
            }

            map[name] = member;
        }

        return false;
    }

    private static bool TryReadText(ref Utf8JsonReader reader, out string? text)
    {
        text = null;
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return true;
            case JsonTokenType.String:
                text = reader.GetString();
                return true;
            default:
                return false;
        }
    }
}
