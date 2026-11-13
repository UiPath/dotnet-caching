using System.Buffers;
using System.Text.Json;
using MessagePack;
using UiPath.Caching.Broadcast;

namespace UiPath.Caching.MessagePack;

/// <summary>
/// An event as the format id byte and a MessagePack map with integer keys: 0 id, 1 type, 2 source, 3 key, 4 the known
/// properties, 5 any other property as JSON bytes. The id is binary when it is a lowercase GUID and the type an integer
/// when it is a known one; peer fields are raw bytes. A key it does not know is skipped, so a later version may add one.
/// </summary>
internal sealed class MessagePackEventFormatter : IEventFormatterProxy<ICacheEvent>, IEventSourceMatcherProvider
{
    private const int IdKey = 0;
    private const int TypeKey = 1;
    private const int SourceKey = 2;
    private const int KeyKey = 3;
    private const int PropertiesKey = 4;
    private const int OtherPropertiesKey = 5;

    private const int VersionProperty = 0;
    private const int ExpirationProperty = 1;
    private const int PeerExpirationProperty = 2;
    private const int MetadataProperty = 3;
    private const int PeerMetadataProperty = 4;
    private const int PeerFieldsProperty = 5;

    // The wire values of the known event types. Append only: a node on another version maps a reordered or reused value to a different type.
    private const int CacheSetType = 0;
    private const int CacheRefreshedType = 1;
    private const int CacheRemovedType = 2;
    private const int TopicInvalidatedType = 3;

    private const int StackProperties = 16;
    private const int InitialBufferBytes = 512;
    private const int MaxRetainedBufferBytes = 64 * 1024;
    private const int MaxInitialCapacity = 16;

    private const long MaxTicks = 3_155_378_975_999_999_999;

    private static readonly MessagePackSecurity Security = MessagePackSecurity.UntrustedData;

    [ThreadStatic] private static ArrayBufferWriter<byte>? _buffer;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2696:Instance members should not write to \"static\" fields", Justification = "The static field is a per-thread ([ThreadStatic]) reusable buffer by design.")]
    public ReadOnlyMemory<byte> Encode(ICacheEvent @event)
    {
        var buffer = _buffer ??= new ArrayBufferWriter<byte>(InitialBufferBytes);
        buffer.ResetWrittenCount();
        try
        {
            Write(@event, buffer);
            // Copied: the caller hands these bytes to Redis and may hold them after this thread reuses the buffer.
            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            if (buffer.Capacity > MaxRetainedBufferBytes)
            {
                _buffer = null;
            }
        }
    }

    public ICacheEvent? Decode(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty || body.Span[0] != CacheValueFormats.MessagePack)
        {
            return null;
        }

        var reader = new MessagePackReader(body[1..]);
        var count = reader.ReadMapHeader();
        var result = new CacheEvent();
        string? key = null;
        var hasKey = false;
        Dictionary<string, object?>? properties = null;
        Dictionary<string, object?>? others = null;
        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadInt32())
            {
                case IdKey:
                    result.Id = ReadId(ref reader);
                    break;
                case TypeKey:
                    result.Type = ReadType(ref reader);
                    break;
                case SourceKey:
                    result.Source = reader.ReadString() is { } text ? new Uri(text, UriKind.RelativeOrAbsolute) : null;
                    break;
                case KeyKey:
                    key = reader.ReadString();
                    hasKey = true;
                    break;
                case PropertiesKey:
                    properties = ReadKnownProperties(ref reader);
                    break;
                case OtherPropertiesKey:
                    others = ReadOtherProperties(ref reader);
                    break;
                default:
                    Skip(ref reader);
                    break;
            }
        }

        if (others is not null)
        {
            properties ??= new Dictionary<string, object?>(others.Count, Security.GetEqualityComparer<string>());
            foreach (var (name, value) in others)
            {
                properties[name] = value;
            }
        }

        if (hasKey || properties is not null)
        {
            result.Data = new CacheEventData(key ?? string.Empty, properties);
        }

        return result;
    }

    public IEventSourceMatcher? CreateSourceMatcher(Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return SourceMatcher.Create(source);
    }

    private static void Write(ICacheEvent @event, ArrayBufferWriter<byte> buffer)
    {
        buffer.GetSpan(1)[0] = CacheValueFormats.MessagePack;
        buffer.Advance(1);
        var writer = new MessagePackWriter(buffer);

        var data = @event.Data;
        var properties = data?.Properties;
        Span<bool> encodedKnown = (properties?.Count ?? 0) <= StackProperties ? stackalloc bool[StackProperties] : new bool[properties!.Count];
        var known = 0;
        var others = 0;
        if (properties is not null)
        {
            var index = 0;
            foreach (var (name, value) in properties)
            {
                var isKnown = IsEncodedKnown(name, value);
                encodedKnown[index++] = isKnown;
                if (isKnown)
                {
                    known++;
                }
                else
                {
                    others++;
                }
            }
        }

        var count = (@event.Id is null ? 0 : 1) + (@event.Type is null ? 0 : 1) + (@event.Source is null ? 0 : 1)
            + (data is null ? 0 : 1) + (properties is null ? 0 : 1) + (others > 0 ? 1 : 0);

        writer.WriteMapHeader(count);
        if (@event.Id is { } id)
        {
            writer.Write(IdKey);
            WriteId(ref writer, id);
        }

        if (@event.Type is { } type)
        {
            writer.Write(TypeKey);
            var code = TypeCode(type);
            if (code >= 0)
            {
                writer.Write(code);
            }
            else
            {
                writer.Write(type);
            }
        }

        if (@event.Source is { } source)
        {
            writer.Write(SourceKey);
            writer.Write(source.OriginalString);
        }

        if (data is not null)
        {
            writer.Write(KeyKey);
            writer.Write(data.Key);
        }

        if (properties is not null)
        {
            writer.Write(PropertiesKey);
            WriteKnownProperties(ref writer, properties, encodedKnown, known);
            if (others > 0)
            {
                writer.Write(OtherPropertiesKey);
                WriteOtherProperties(ref writer, properties, encodedKnown, others);
            }
        }

        writer.Flush();
    }

    private static int TypeCode(string type) => type switch
    {
        KnownEventTypes.CacheSet => CacheSetType,
        KnownEventTypes.CacheRefreshed => CacheRefreshedType,
        KnownEventTypes.CacheRemoved => CacheRemovedType,
        KnownEventTypes.TopicInvalidated => TopicInvalidatedType,
        _ => -1,
    };

    private static string? TypeName(int code) => code switch
    {
        CacheSetType => KnownEventTypes.CacheSet,
        CacheRefreshedType => KnownEventTypes.CacheRefreshed,
        CacheRemovedType => KnownEventTypes.CacheRemoved,
        TopicInvalidatedType => KnownEventTypes.TopicInvalidated,
        _ => null,
    };

    private static bool IsEncodedKnown(string name, object? value) => name switch
    {
        KnownFieldNames.VersionKey => value is long,
        KnownFieldNames.ExpirationKey or KnownFieldNames.PeerExpirationKey => value is DateTimeOffset,
        KnownFieldNames.MetadataKey or KnownFieldNames.PeerMetadataKey => value is IEnumerable<KeyValuePair<string, string?>>,
        KnownFieldNames.PeerFieldsKey => value is IEnumerable<KeyValuePair<string, string?>> or IEnumerable<KeyValuePair<string, byte[]?>>,
        _ => false,
    };

    private static void WriteId(ref MessagePackWriter writer, string id)
    {
        Span<char> text = stackalloc char[36];
        if (id.Length == 36 && Guid.TryParseExact(id, "D", out var guid) && guid.TryFormat(text, out _, "D") && text.SequenceEqual(id))
        {
            Span<byte> bytes = stackalloc byte[16];
            guid.TryWriteBytes(bytes);
            writer.Write(bytes);
        }
        else
        {
            writer.Write(id);
        }
    }

    private static string? ReadId(ref MessagePackReader reader)
    {
        if (reader.NextMessagePackType == MessagePackType.Binary)
        {
            var bytes = reader.ReadBytes()!.Value;
            if (bytes.Length != 16)
            {
                throw new MessagePackSerializationException("An event id as binary is 16 bytes.");
            }

            Span<byte> span = stackalloc byte[16];
            bytes.CopyTo(span);
            return new Guid(span).ToString();
        }

        return reader.ReadString();
    }

    private static string? ReadType(ref MessagePackReader reader)
    {
        if (reader.NextMessagePackType == MessagePackType.Integer)
        {
            var code = reader.ReadInt32();
            return TypeName(code) ?? throw new MessagePackSerializationException($"Unknown event type {code}.");
        }

        return reader.ReadString();
    }

    private static void WriteKnownProperties(ref MessagePackWriter writer, IDictionary<string, object?> properties, scoped ReadOnlySpan<bool> encodedKnown, int count)
    {
        writer.WriteMapHeader(count);
        var index = 0;
        foreach (var (name, value) in properties)
        {
            if (!encodedKnown[index++])
            {
                continue;
            }

            switch (name)
            {
                case KnownFieldNames.VersionKey:
                    writer.Write(VersionProperty);
                    writer.Write((long)value!);
                    break;
                case KnownFieldNames.ExpirationKey:
                    writer.Write(ExpirationProperty);
                    writer.Write(((DateTimeOffset)value!).UtcTicks);
                    break;
                case KnownFieldNames.PeerExpirationKey:
                    writer.Write(PeerExpirationProperty);
                    writer.Write(((DateTimeOffset)value!).UtcTicks);
                    break;
                case KnownFieldNames.MetadataKey:
                    writer.Write(MetadataProperty);
                    WriteTextMap(ref writer, (IEnumerable<KeyValuePair<string, string?>>)value!);
                    break;
                case KnownFieldNames.PeerMetadataKey:
                    writer.Write(PeerMetadataProperty);
                    WriteTextMap(ref writer, (IEnumerable<KeyValuePair<string, string?>>)value!);
                    break;
                default:
                    writer.Write(PeerFieldsProperty);
                    WritePeerFields(ref writer, value!);
                    break;
            }
        }
    }

    private static void WriteTextMap(ref MessagePackWriter writer, IEnumerable<KeyValuePair<string, string?>> map)
    {
        writer.WriteMapHeader(map.Count());
        foreach (var (name, text) in map)
        {
            writer.Write(name);
            writer.Write(text);
        }
    }

    private static void WritePeerFields(ref MessagePackWriter writer, object fields)
    {
        if (fields is IEnumerable<KeyValuePair<string, byte[]?>> raw)
        {
            writer.WriteMapHeader(raw.Count());
            foreach (var (name, bytes) in raw)
            {
                writer.Write(name);
                writer.Write(bytes);
            }

            return;
        }

        var encoded = (IEnumerable<KeyValuePair<string, string?>>)fields;
        writer.WriteMapHeader(encoded.Count());
        foreach (var (name, text) in encoded)
        {
            writer.Write(name);
            if (text is null)
            {
                writer.WriteNil();
            }
            else
            {
                writer.Write(Convert.FromBase64String(text));
            }
        }
    }

    private static void WriteOtherProperties(ref MessagePackWriter writer, IDictionary<string, object?> properties, scoped ReadOnlySpan<bool> encodedKnown, int count)
    {
        writer.WriteMapHeader(count);
        var index = 0;
        foreach (var (name, value) in properties)
        {
            if (encodedKnown[index++])
            {
                continue;
            }

            writer.Write(name);
            if (value is null)
            {
                writer.WriteNil();
            }
            else
            {
                writer.Write(JsonSerializer.SerializeToUtf8Bytes(value));
            }
        }
    }

    private static Dictionary<string, object?> ReadKnownProperties(ref MessagePackReader reader)
    {
        var count = reader.ReadMapHeader();
        var properties = new Dictionary<string, object?>(Math.Min(count, MaxInitialCapacity), Security.GetEqualityComparer<string>());
        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadInt32())
            {
                case VersionProperty:
                    properties[KnownFieldNames.VersionKey] = reader.ReadInt64();
                    break;
                case ExpirationProperty:
                    properties[KnownFieldNames.ExpirationKey] = ReadInstant(ref reader);
                    break;
                case PeerExpirationProperty:
                    properties[KnownFieldNames.PeerExpirationKey] = ReadInstant(ref reader);
                    break;
                case MetadataProperty:
                    properties[KnownFieldNames.MetadataKey] = ReadTextMap(ref reader);
                    break;
                case PeerMetadataProperty:
                    properties[KnownFieldNames.PeerMetadataKey] = ReadTextMap(ref reader);
                    break;
                case PeerFieldsProperty:
                    properties[KnownFieldNames.PeerFieldsKey] = ReadPeerFields(ref reader);
                    break;
                default:
                    Skip(ref reader);
                    break;
            }
        }

        return properties;
    }

    private static DateTimeOffset ReadInstant(ref MessagePackReader reader)
    {
        var ticks = reader.ReadInt64();
        if (ticks is < 0 or > MaxTicks)
        {
            throw new MessagePackSerializationException("An expiry outside the range of a date.");
        }

        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static Dictionary<string, string?>? ReadTextMap(ref MessagePackReader reader)
    {
        if (reader.TryReadNil())
        {
            return null;
        }

        var count = reader.ReadMapHeader();
        var map = new Dictionary<string, string?>(Math.Min(count, MaxInitialCapacity), Security.GetEqualityComparer<string>());
        for (var i = 0; i < count; i++)
        {
            map[reader.ReadString() ?? throw new MessagePackSerializationException("A map key is not null.")] = reader.ReadString();
        }

        return map;
    }

    private static Dictionary<string, byte[]?>? ReadPeerFields(ref MessagePackReader reader)
    {
        if (reader.TryReadNil())
        {
            return null;
        }

        var count = reader.ReadMapHeader();
        var map = new Dictionary<string, byte[]?>(Math.Min(count, MaxInitialCapacity), Security.GetEqualityComparer<string>());
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString() ?? throw new MessagePackSerializationException("A map key is not null.");
            map[name] = reader.TryReadNil() ? null : reader.ReadBytes()!.Value.ToArray();
        }

        return map;
    }

    private static Dictionary<string, object?> ReadOtherProperties(ref MessagePackReader reader)
    {
        var count = reader.ReadMapHeader();
        var map = new Dictionary<string, object?>(Math.Min(count, MaxInitialCapacity), Security.GetEqualityComparer<string>());
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString() ?? throw new MessagePackSerializationException("A property name is not null.");
            map[name] = reader.TryReadNil() ? null : JsonSerializer.Deserialize<JsonElement>(reader.ReadBytes()!.Value.ToArray());
        }

        return map;
    }

    private static void Skip(ref MessagePackReader reader)
    {
        Security.DepthStep(ref reader);
        try
        {
            switch (reader.NextMessagePackType)
            {
                case MessagePackType.Array:
                    for (var n = reader.ReadArrayHeader(); n > 0; n--)
                    {
                        Skip(ref reader);
                    }

                    break;
                case MessagePackType.Map:
                    for (var n = reader.ReadMapHeader(); n > 0; n--)
                    {
                        Skip(ref reader);
                        Skip(ref reader);
                    }

                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        finally
        {
            reader.Depth--;
        }
    }

    /// <summary>Finds the source in the top-level map and compares its text with one source's, allocating nothing.</summary>
    private sealed class SourceMatcher : IEventSourceMatcher
    {
        private readonly byte[][] _texts;

        private SourceMatcher(byte[][] texts) => _texts = texts;

        public static SourceMatcher? Create(Uri source)
        {
            var normalized = source.IsAbsoluteUri ? source.GetComponents(UriComponents.AbsoluteUri, UriFormat.SafeUnescaped) : source.OriginalString;
            string[] candidates = normalized == source.OriginalString ? [normalized] : [source.OriginalString, normalized];
            var texts = new List<byte[]>(candidates.Length);
            foreach (var text in candidates)
            {
                if (text.Length > 0 && IsSame(text, source))
                {
                    texts.Add(System.Text.Encoding.UTF8.GetBytes(text));
                }
            }

            return texts.Count == 0 ? null : new SourceMatcher([.. texts]);
        }

        /// <summary>True when the body is a map whose source key, present once, is a string equal to one of the texts; any other shape is not this source's.</summary>
        public bool IsFromSource(ReadOnlyMemory<byte> payload)
        {
            if (payload.IsEmpty || payload.Span[0] != CacheValueFormats.MessagePack)
            {
                return false;
            }

            try
            {
                var reader = new MessagePackReader(payload[1..]);
                var matched = false;
                for (var count = reader.ReadMapHeader(); count > 0; count--)
                {
                    if (reader.ReadInt32() != SourceKey)
                    {
                        Skip(ref reader);
                    }
                    else if (matched || reader.NextMessagePackType != MessagePackType.String || !reader.TryReadStringSpan(out var text) || !Matches(text))
                    {
                        // A second source key would win the decode, so it is read past only to say no.
                        return false;
                    }
                    else
                    {
                        matched = true;
                    }
                }

                return matched && reader.End;
            }
            // Malformed input of any kind is not this source's: the decode and its drop handling take it from here.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return false;
            }
        }

        private static bool IsSame(string text, Uri source)
        {
            try
            {
                return new CacheEvent { Source = new Uri(text, UriKind.RelativeOrAbsolute) }.SameSource(source);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }

        private bool Matches(ReadOnlySpan<byte> text)
        {
            foreach (var candidate in _texts)
            {
                if (text.SequenceEqual(candidate))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
