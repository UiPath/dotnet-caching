using System.Text.Json;

namespace UiPath.Caching;

/// <summary>
/// Stores byte payloads verbatim and UTF-8 JSON for everything else, decided by the type argument.
/// <c>AddDistributedCache</c> gives this to its own provider, whose payload is caller bytes the
/// caller has already serialized.
/// </summary>
/// <remarks>
/// Registering this app-wide is a wire-format change: it returns stored bytes as-is rather than
/// base64-decoding what <see cref="SystemJsonByteSerializerProxy"/> wrote, and cannot detect the
/// difference. Relocate the keyspace when switching an existing deployment over.
/// </remarks>
public class RawByteSerializerProxy(JsonSerializerOptions? options = null)
    : SystemJsonByteSerializerProxy(options)
{
    public override byte[]? Serialize(object? value) => value switch
    {
        byte[] bytes => bytes,
        ReadOnlyMemory<byte> memory => memory.ToArray(),
        Memory<byte> memory => memory.ToArray(),
        _ => base.Serialize(value),
    };

    /// <summary>
    /// Memory in, the same memory out — no array, no copy. The type tests are on <c>typeof(T)</c> rather
    /// than a pattern on a boxed value so the JIT resolves them per instantiation and the cast through
    /// <see cref="object"/> costs nothing. A byte array falls through to <see cref="Serialize"/>, which hands
    /// back the array itself; everything else is JSON, as it is there. <c>where T : default</c> keeps
    /// <c>T?</c> the unconstrained annotation the interface declares; in an override it would otherwise read
    /// as <see cref="Nullable{T}"/>.
    /// </summary>
    public override ReadOnlyMemory<byte> SerializeToMemory<T>(T? value) where T : default
    {
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            return (ReadOnlyMemory<byte>)(object)value!;
        }
        if (typeof(T) == typeof(Memory<byte>))
        {
            return (Memory<byte>)(object)value!;
        }
        return base.SerializeToMemory(value);
    }

    /// <remarks>
    /// Declared as <c>T</c>, not <c>T?</c>: an override cannot restate that annotation on an
    /// unconstrained type parameter, so the maybe-null contract comes from the base and the
    /// suppressions below are what that costs.
    /// </remarks>
    public override T Deserialize<T>(byte[]? value)
    {
        if (value is null)
        {
            return default!;
        }
        if (typeof(T) == typeof(byte[]))
        {
            return (T)(object)value;
        }
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            return (T)(object)new ReadOnlyMemory<byte>(value);
        }
        if (typeof(T) == typeof(Memory<byte>))
        {
            return (T)(object)new Memory<byte>(value);
        }
        return base.Deserialize<T>(value)!;
    }
}
