using MessagePack;
using MessagePack.Resolvers;

namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>An in-place format that is not the envelope: the value as plain MessagePack, with no type name.</summary>
internal sealed class MessagePackInPlaceProxy : ISerializerProxy<byte[]>
{
    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolverAllowPrivate.Instance);

    public static byte[] Write(object value) => MessagePackSerializer.Serialize(value.GetType(), value, Options);

    public byte[]? Serialize(object? value) => value is null ? null : Write(value);

    public T? Deserialize<T>(byte[]? value) => value is null || value.Length == 0 ? default : MessagePackSerializer.Deserialize<T>(value, Options);

    public bool TryDeserialize<T>(string? value, out T? result)
    {
        result = default;
        return false;
    }

    public bool TryDeserialize<T>(object? value, out T? result)
    {
        result = default;
        return false;
    }
}
