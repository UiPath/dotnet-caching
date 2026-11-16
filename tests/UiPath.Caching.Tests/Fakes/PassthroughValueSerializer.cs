using System.Buffers;
using System.Text;
using System.Text.Json;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>Writes a <see cref="byte"/> array or string as its raw bytes under its first id, anything else as JSON text, and reads them back.</summary>
internal sealed class PassthroughValueSerializer(params int[] ids) : ICacheValueSerializer
{
    private int _serialized;
    private int _deserialized;

    public IReadOnlyList<int> FormatIds { get; } = ids.Length == 0 ? [0x10] : ids;

    public int Serialized => Volatile.Read(ref _serialized);

    public int Deserialized => Volatile.Read(ref _deserialized);

    public int LastFormatId { get; private set; }

    public byte[] LastBody { get; private set; } = [];

    public int Serialize<T>(T value, IBufferWriter<byte> target)
    {
        Interlocked.Increment(ref _serialized);
        var bytes = value switch
        {
            byte[] raw => raw,
            string text => Encoding.UTF8.GetBytes(text),
            _ => JsonSerializer.SerializeToUtf8Bytes(value),
        };
        target.Write(bytes);
        return FormatIds[0];
    }

    public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source)
    {
        Interlocked.Increment(ref _deserialized);
        LastFormatId = formatId;
        var body = source.ToArray();
        LastBody = body;
        if (typeof(T) == typeof(byte[]))
        {
            return (T)(object)body;
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)Encoding.UTF8.GetString(body);
        }

        return JsonSerializer.Deserialize<T>(body);
    }
}
