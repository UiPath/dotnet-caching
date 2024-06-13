using System.Diagnostics;

namespace UiPath.Platform.Caching;

public class NotCacheableException : Exception
{
    public NotCacheableException(Type type)
        : base($"Type {type} is not cacheable. Use class or nullable struct")
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
        if(!IsNullable(type))
        {
            Throw(type);
        }
    }

    [DoesNotReturn]
    private static void Throw(Type type) => throw new NotCacheableException(type);

    private static bool IsNullable(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
}
