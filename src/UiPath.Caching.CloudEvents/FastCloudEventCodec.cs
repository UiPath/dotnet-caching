using System.Buffers;
using System.Text.Json;

namespace UiPath.Caching.CloudEvents;

internal sealed class FastCloudEventCodec
{
    private const int MaxRetainedBufferBytes = 64 * 1024;

    private const int IdSeen = 1;
    private const int TypeSeen = 2;
    private const int SourceSeen = 4;
    private const int ContentTypeSeen = 8;
    private const int SpecVersionSeen = 16;
    private const int DataSeen = 32;
    private const int AllSeen = IdSeen | TypeSeen | SourceSeen | ContentTypeSeen | SpecVersionSeen | DataSeen;

    [ThreadStatic] private static ArrayBufferWriter<byte>? _buffer;
    [ThreadStatic] private static Utf8JsonWriter? _writer;

    private readonly Utf8TextCache<string> _types = new(static text => text);
    private readonly Utf8TextCache<Uri> _sources = new(static text => new Uri(text, UriKind.RelativeOrAbsolute));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Kept as an instance member: EventFormatterProxy holds the codec instance to opt in to the fast path.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Kept as an instance member: EventFormatterProxy holds the codec instance to opt in to the fast path.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2696:Instance members should not write to \"static\" fields", Justification = "The static fields are per-thread ([ThreadStatic]) reusable buffers by design.")]
    public bool TryEncode(CloudEvent cloudEvent, out ReadOnlyMemory<byte> bytes)
    {
        bytes = default;
        if (cloudEvent.Data is not CacheEventData data
            || cloudEvent.SpecVersion != CloudEventsSpecVersion.V1_0
            || cloudEvent.Id is not { } id
            || cloudEvent.Type is not { } type
            || cloudEvent.Source is not { } source)
        {
            return false;
        }

        var buffer = _buffer ??= new ArrayBufferWriter<byte>(512);
        buffer.ResetWrittenCount();
        var writer = _writer;
        if (writer is null)
        {
            _writer = writer = new Utf8JsonWriter(buffer);
        }
        else
        {
            writer.Reset(buffer);
        }

        try
        {
            if (!TryWrite(writer, cloudEvent, id, type, source, data))
            {
                return false;
            }

            writer.Flush();
            // Copy: the caller hands these bytes to Redis and may hold them after this thread reuses the buffer.
            bytes = buffer.WrittenSpan.ToArray();
            return true;
        }
        finally
        {
            if (buffer.Capacity > MaxRetainedBufferBytes)
            {
                _buffer = null;
                _writer = null;
            }
        }
    }

    public bool TryDecode(ReadOnlySpan<byte> body, out CacheCloudEventWrapper? result)
    {
        result = null;
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            string? id = null, type = null;
            Uri? source = null;
            CacheEventData? data = null;
            var seen = 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var flag = FlagOf(ref reader);
                if (flag == 0 || (seen & flag) != 0 || !TryReadValue(ref reader, flag, ref id, ref type, ref source, ref data))
                {
                    return false;
                }

                seen |= flag;
            }

            // reader.Read() is false only at the end of the input, so trailing content is left to the stock decoder.
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || seen != AllSeen
                || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type) || string.IsNullOrEmpty(source?.OriginalString) || data is null)
            {
                return false;
            }

            result = new CacheCloudEventWrapper(id, type, source, data);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static int FlagOf(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("id"u8))
        {
            return IdSeen;
        }

        if (reader.ValueTextEquals("type"u8))
        {
            return TypeSeen;
        }

        if (reader.ValueTextEquals("source"u8))
        {
            return SourceSeen;
        }

        if (reader.ValueTextEquals("datacontenttype"u8))
        {
            return ContentTypeSeen;
        }

        if (reader.ValueTextEquals("specversion"u8))
        {
            return SpecVersionSeen;
        }

        return reader.ValueTextEquals("data"u8) ? DataSeen : 0;
    }

    private static bool TryWrite(Utf8JsonWriter writer, CloudEvent cloudEvent, string id, string type, Uri source, CacheEventData data)
    {
        writer.WriteStartObject();
        writer.WriteString("specversion"u8, "1.0");
        var seen = 0;
        // The stock formatter writes attributes in the order they were set, so the same order is walked here.
        foreach (var (attribute, _) in cloudEvent.GetPopulatedAttributes())
        {
            switch (attribute.Name)
            {
                case "id":
                    writer.WriteString("id"u8, id);
                    seen |= IdSeen;
                    break;
                case "type":
                    writer.WriteString("type"u8, type);
                    seen |= TypeSeen;
                    break;
                case "source":
                    writer.WriteString("source"u8, source.OriginalString);
                    seen |= SourceSeen;
                    break;
                case "datacontenttype" when cloudEvent.DataContentType == "application/json":
                    writer.WriteString("datacontenttype"u8, "application/json");
                    seen |= ContentTypeSeen;
                    break;
                default:
                    return false;
            }
        }

        if (seen != (IdSeen | TypeSeen | SourceSeen | ContentTypeSeen))
        {
            return false;
        }

        writer.WritePropertyName("data"u8);
        JsonSerializer.Serialize(writer, data);
        writer.WriteEndObject();
        return true;
    }

    private bool TryReadValue(ref Utf8JsonReader reader, int flag, ref string? id, ref string? type, ref Uri? source, ref CacheEventData? data)
    {
        reader.Read();
        if (flag == DataSeen ? reader.TokenType != JsonTokenType.StartObject : reader.TokenType != JsonTokenType.String)
        {
            return false;
        }

        switch (flag)
        {
            case SpecVersionSeen when !reader.ValueTextEquals("1.0"u8):
            case ContentTypeSeen when !reader.ValueTextEquals("application/json"u8):
                return false;
            case IdSeen:
                id = reader.GetString();
                break;
            case TypeSeen:
                type = _types.Get(ref reader);
                break;
            case SourceSeen:
                source = _sources.Get(ref reader);
                break;
            case DataSeen:
                data = JsonSerializer.Deserialize<CacheEventData>(ref reader);
                break;
        }

        return true;
    }

    private sealed class Utf8TextCache<T>(Func<string, T> create)
        where T : class
    {
        private const int MaxEntries = 16;
        private const int MaxTextBytes = 256;

        private readonly object _lock = new();
        private (byte[] Raw, T Value)[] _entries = [];

        public T Get(ref Utf8JsonReader reader)
        {
            if (reader.HasValueSequence || reader.ValueIsEscaped)
            {
                return create(reader.GetString()!);
            }

            var span = reader.ValueSpan;
            var entries = Volatile.Read(ref _entries);
            foreach (var (raw, value) in entries)
            {
                if (span.SequenceEqual(raw))
                {
                    return value;
                }
            }

            var created = create(reader.GetString()!);
            // Once full the cache is left as it is: a miss then costs only the lookup and the value itself.
            if (entries.Length < MaxEntries && span.Length <= MaxTextBytes)
            {
                Add(span.ToArray(), created);
            }

            return created;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3267:Loops should be simplified with LINQ expressions", Justification = "Any would allocate a closure capturing raw on the decode path.")]
        private void Add(byte[] raw, T value)
        {
            lock (_lock)
            {
                var current = _entries;
                foreach (var (known, _) in current)
                {
                    if (known.AsSpan().SequenceEqual(raw))
                    {
                        return;
                    }
                }

                if (current.Length >= MaxEntries)
                {
                    return;
                }

                var next = new (byte[] Raw, T Value)[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[^1] = (raw, value);
                Volatile.Write(ref _entries, next);
            }
        }
    }
}
