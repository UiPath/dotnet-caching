using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using UiPath.Caching.Broadcast;

namespace UiPath.Caching.PluginContract.Tests.Acme;

/// <summary>The id byte, then JSON where every property carries a type tag so the typed ones come back as the types the cache reads.</summary>
public sealed class AcmeEventFormat(byte? idByte = null) : IEventFormatterProxy<ICacheEvent>, IDisposable
{
    private readonly byte _id = idByte ?? AcmeFormats.Event;
    private readonly ConcurrentQueue<ICacheEvent> _decoded = [];
    private int _disposals;

    public IReadOnlyCollection<ICacheEvent> Decoded => _decoded;

    public int Disposals => _disposals;

    internal Action<ICacheEvent>? OnDecode { get; set; }

    public void Dispose() => Interlocked.Increment(ref _disposals);

    public ReadOnlyMemory<byte> Encode(ICacheEvent @event)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte(_id);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("id", @event.Id);
            json.WriteString("type", @event.Type);
            json.WriteString("source", @event.Source?.OriginalString);
            json.WriteString("key", @event.Key);
            json.WriteStartObject("props");
            foreach (var (name, value) in @event.Data?.Properties ?? new Dictionary<string, object?>())
            {
                json.WriteStartObject(name);
                WriteValue(json, value);
                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    public ICacheEvent? Decode(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty || body.Span[0] != _id)
        {
            return null;
        }

        using var document = JsonDocument.Parse(body[1..]);
        var root = document.RootElement;
        var properties = new Dictionary<string, object?>();
        foreach (var property in root.GetProperty("props").EnumerateObject())
        {
            properties[property.Name] = ReadValue(property.Value);
        }

        var decoded = new CacheEvent
        {
            Id = root.GetProperty("id").GetString(),
            Type = root.GetProperty("type").GetString(),
            Source = root.GetProperty("source").GetString() is { } source ? new Uri(source, UriKind.RelativeOrAbsolute) : null,
            Data = new CacheEventData(root.GetProperty("key").GetString() ?? string.Empty, properties.Count == 0 ? null : properties),
        };
        _decoded.Enqueue(decoded);
        OnDecode?.Invoke(decoded);
        return decoded;
    }

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNull("n");
                break;
            case long number:
                json.WriteNumber("l", number);
                break;
            case DateTimeOffset moment:
                json.WriteString("d", moment.ToString("O", CultureInfo.InvariantCulture));
                break;
            case IEnumerable<KeyValuePair<string, string?>> strings:
                json.WriteStartObject("s");
                foreach (var (field, text) in strings)
                {
                    json.WriteString(field, text);
                }

                json.WriteEndObject();
                break;
            case IEnumerable<KeyValuePair<string, byte[]?>> bytes:
                json.WriteStartObject("b");
                foreach (var (field, data) in bytes)
                {
                    json.WriteString(field, data is null ? null : Convert.ToBase64String(data));
                }

                json.WriteEndObject();
                break;
            default:
                json.WritePropertyName("j");
                JsonSerializer.Serialize(json, value);
                break;
        }
    }

    private static object? ReadValue(JsonElement tagged)
    {
        var entry = tagged.EnumerateObject().Single();
        return entry.Name switch
        {
            "n" => null,
            "l" => entry.Value.GetInt64(),
            "d" => DateTimeOffset.Parse(entry.Value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            "s" => entry.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()),
            "b" => entry.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? null : Convert.FromBase64String(p.Value.GetString()!)),
            _ => entry.Value.Clone(),
        };
    }
}
