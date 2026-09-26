using System.Buffers;
using System.Text.Json;

namespace UiPath.Caching;

/// <summary>Writes JSON into an array rented from <see cref="ArrayPool{T}.Shared"/>. One per thread, since serializing is synchronous; a nested call takes a fresh one.</summary>
internal sealed class PooledJsonWriter : IBufferWriter<byte>
{
    private const int MinimumBufferSize = 256;
    private const int DefaultMaxDepth = 64;

    [ThreadStatic]
    private static PooledJsonWriter? _cached;

    private Utf8JsonWriter? _json;
    private JsonSerializerOptions? _jsonOptions;
    private byte[] _buffer = [];
    private int _written;

    /// <summary>The bytes <see cref="JsonSerializer.SerializeToUtf8Bytes{TValue}(TValue, JsonSerializerOptions?)"/> writes for <c>(object?)value</c>: null goes through <see cref="object"/>, and the runtime type picks the contract.</summary>
    public static SerializedPayload Serialize<T>(T? value, JsonSerializerOptions? options)
    {
        var writer = _cached ?? new PooledJsonWriter();
        _cached = null;
        try
        {
            var json = writer.JsonFor(options ?? JsonSerializerOptions.Default);
            if (value is null)
            {
                JsonSerializer.Serialize<object?>(json, null, options);
            }
            else if (typeof(T).IsValueType ? !IsNullable<T>.Value : value.GetType() == typeof(T))
            {
                JsonSerializer.Serialize(json, value, options);
            }
            else
            {
                // A boxed Nullable<U> is a U, so that is the contract Serialize(object) would use.
                JsonSerializer.Serialize(json, value, value.GetType(), options);
            }
            json.Flush();
            return writer.Detach();
        }
        catch
        {
            writer.Clear();
            throw;
        }
        finally
        {
            _cached = writer;
        }
    }

    public void Advance(int count) => _written += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    /// <summary>Mirrors the writer settings <see cref="JsonSerializer"/> derives from the options, so the bytes match.</summary>
    private static JsonWriterOptions WriterOptions(JsonSerializerOptions options) => new()
    {
        Encoder = options.Encoder,
        Indented = options.WriteIndented,
        MaxDepth = options.MaxDepth == 0 ? DefaultMaxDepth : options.MaxDepth,
#if NET9_0_OR_GREATER
        IndentCharacter = options.IndentCharacter,
        IndentSize = options.IndentSize,
        NewLine = options.NewLine,
#endif
        SkipValidation = true,
    };

    private Utf8JsonWriter JsonFor(JsonSerializerOptions options)
    {
        if (_json is null || !ReferenceEquals(_jsonOptions, options))
        {
            _json = new Utf8JsonWriter(this, WriterOptions(options));
            _jsonOptions = options;
        }
        else
        {
            _json.Reset(this);
        }
        return _json;
    }

    private SerializedPayload Detach()
    {
        var payload = SerializedPayload.FromRented(_buffer, _written);
        _buffer = [];
        _written = 0;
        return payload;
    }

    private void Clear()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }
        _buffer = [];
        _written = 0;
    }

    private void EnsureCapacity(int sizeHint)
    {
        var required = _written + Math.Max(sizeHint, 1);
        if (required <= _buffer.Length)
        {
            return;
        }

        var doubled = (int)Math.Min((long)_buffer.Length * 2, Array.MaxLength);
        var next = ArrayPool<byte>.Shared.Rent(Math.Max(required, Math.Max(doubled, MinimumBufferSize)));
        _buffer.AsSpan(0, _written).CopyTo(next);
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }
        _buffer = next;
    }

    private static class IsNullable<T>
    {
        public static readonly bool Value = Nullable.GetUnderlyingType(typeof(T)) is not null;
    }
}
