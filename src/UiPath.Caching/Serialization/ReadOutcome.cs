namespace UiPath.Caching.Serialization;

internal readonly struct ReadOutcome<T>
{
    private ReadOutcome(T? value, int unreadableFormat, Exception? failure)
    {
        Value = value;
        UnreadableFormat = unreadableFormat;
        Failure = failure;
    }

    public T? Value { get; }

    /// <summary>The format id no registered serializer reads, or -1 for a header cut short; 0 otherwise. Only such data is removed.</summary>
    public int UnreadableFormat { get; }

    /// <summary>What a registered serializer threw; null otherwise. A miss that keeps the key, since the data may be fine for another version of the type.</summary>
    public Exception? Failure { get; }

    public bool IsUnreadable => UnreadableFormat != 0;

    public bool IsMiss => IsUnreadable || Failure is not null;

    public static ReadOutcome<T> Read(T? value) => new(value, 0, null);

    public static ReadOutcome<T> Unreadable(int formatId) => new(default, formatId, null);

    public static ReadOutcome<T> Failed(Exception failure) => new(default, 0, failure);
}
