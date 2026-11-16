namespace UiPath.Caching.Tests;

internal static class RegistryEntry
{
    public static (string Name, ICacheValueSerializer Serializer, IReadOnlyList<int> Ids) Of(string name, ICacheValueSerializer serializer) =>
        (name, serializer, serializer.FormatIds);
}
