namespace UiPath.Caching;

/// <summary>Serializer that can lend memory instead of an array. The memory is borrowed: it may alias the value and is valid only until the operation completes, so consumers must not retain it.</summary>
public interface IMemorySerializerProxy : ISerializerProxy<byte[]>
{
    ReadOnlyMemory<byte> SerializeToMemory<T>(T? value);
}
