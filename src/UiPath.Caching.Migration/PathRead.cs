namespace UiPath.Caching.Migration;

internal readonly record struct PathRead<T>(ReadOutcome Kind, T? Value, DateTimeOffset? Expiration)
    where T : class
{
    public static PathRead<T> Of(ReadOutcome kind) => new(kind, null, null);
}
