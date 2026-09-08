using System.Diagnostics;

namespace UiPath.Caching;

public class NotCacheableException : Exception
{
    public NotCacheableException(Type type)
        : base($"Type {type} is not cacheable. Use a class, a nullable struct, or ReadOnlyMemory<byte>")
    {
    }

    public NotCacheableException()
    {
    }

    public NotCacheableException(string? message) : base(message)
    {
    }

    public NotCacheableException(string? message, Exception? innerException) : base(message, innerException)
    {
    }

    [DebuggerStepThrough]
    public static void ThrowIfNotCacheable<T>() =>
        ThrowIfNotCacheable(typeof(T));

    [DebuggerStepThrough]
    public static void ThrowIfNotCacheable(Type type)
    {
        if(!IsCacheable(type))
        {
            Throw(type);
        }
    }

    [DoesNotReturn]
    private static void Throw(Type type) => throw new NotCacheableException(type);

    /// <summary>A value type is cacheable only if its default can stand for "absent"; <see cref="ReadOnlyMemory{T}"/> of bytes qualifies because empty memory already reads as a miss.</summary>
    private static bool IsCacheable(Type type) =>
        !type.IsValueType || Nullable.GetUnderlyingType(type) != null || type == typeof(ReadOnlyMemory<byte>);
}
