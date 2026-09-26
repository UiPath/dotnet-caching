using System.Buffers;

namespace UiPath.Caching;

/// <summary>Serialized bytes, possibly in a pooled buffer. Dispose it exactly once, after the write that reads <see cref="Memory"/> has completed.</summary>
internal readonly struct SerializedPayload : IDisposable
{
    private readonly byte[]? _rented;

    /// <summary>Memory the caller keeps; disposing does nothing.</summary>
    public SerializedPayload(ReadOnlyMemory<byte> memory)
    {
        Memory = memory;
        _rented = null;
    }

    private SerializedPayload(byte[] rented, int length)
    {
        _rented = rented;
        Memory = new ReadOnlyMemory<byte>(rented, 0, length);
    }

    public ReadOnlyMemory<byte> Memory { get; }

    public void Dispose()
    {
        if (_rented is not null)
        {
            ArrayPool<byte>.Shared.Return(_rented);
        }
    }

    /// <summary>Pooled for the built-in JSON serializer itself; a subclass, like any other serializer, lends its own memory.</summary>
    internal static SerializedPayload Serialize<T>(IMemorySerializerProxy serializer, T? value) =>
        serializer is SystemJsonByteSerializerProxy json && json.GetType() == typeof(SystemJsonByteSerializerProxy)
            ? PooledJsonWriter.Serialize(value, json.Options)
            : new(serializer.SerializeToMemory(value));

    internal static SerializedPayload FromRented(byte[] rented, int length) => new(rented, length);
}
