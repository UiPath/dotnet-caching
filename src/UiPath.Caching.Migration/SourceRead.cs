namespace UiPath.Caching.Migration;

public readonly record struct SourceRead<T>(ReadOutcome Kind, T? Value)
    where T : class
{
    public static SourceRead<T> Absent => new(ReadOutcome.Absent, null);

    public static SourceRead<T> Unreadable => new(ReadOutcome.Unreadable, null);

    public static SourceRead<T> Unavailable => new(ReadOutcome.Unavailable, null);

    public static SourceRead<T> Found(T value) => new(ReadOutcome.Found, value);
}
