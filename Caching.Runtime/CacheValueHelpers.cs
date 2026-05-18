namespace UiPath.Platform.Caching;

internal static class CacheValueHelpers
{
    public static bool IsDefault<T>(T value) =>
        EqualityComparer<T>.Default.Equals(value, default);
}
