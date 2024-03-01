namespace UiPath.Platform.Caching;

public interface ISerializerProxy
{
    string? Serialize(object? value);

    T? Deserialize<T>(string? value);

    bool TryDeserialize<T>(string? value, out T? result);

    bool TryDeserialize<T>(object? value, out T? result);
}
