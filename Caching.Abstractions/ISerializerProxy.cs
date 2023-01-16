namespace UiPath.Platform.Caching;

public interface ISerializerProxy
{
    string? Serialize(object? value);

    T? Deserialize<T>(string? value);
}
