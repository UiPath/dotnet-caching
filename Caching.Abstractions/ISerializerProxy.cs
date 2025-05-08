namespace UiPath.Platform.Caching;

public interface ISerializerProxy<T1>
{
    T1? Serialize(object? value);

    T? Deserialize<T>(T1? value);

    bool TryDeserialize<T>(string? value, out T? result);

    bool TryDeserialize<T>(object? value, out T? result);
}
