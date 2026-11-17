namespace UiPath.Caching.Serialization;

internal readonly struct ReadOutcome<T>
{
    private ReadOutcome(T? value, int unreadableFormat, int excludedFormat, Exception? failure, int sourceFormat = 0)
    {
        Value = value;
        SourceFormat = sourceFormat;
        UnreadableFormat = unreadableFormat;
        ExcludedFormat = excludedFormat;
        Failure = failure;
    }

    public T? Value { get; }

    /// <summary>The format id a read value was stored under; 0 for headerless JSON.</summary>
    public int SourceFormat { get; }

    /// <summary>The format id no registered serializer reads, or -1 for a header cut short; 0 otherwise. Only such data is removed.</summary>
    public int UnreadableFormat { get; }

    /// <summary>What a registered serializer threw; null otherwise. A miss that keeps the key, since the data may be fine for another version of the type.</summary>
    public Exception? Failure { get; }

    /// <summary>The format id of a value the policy's read list leaves out, or <see cref="ReadableFormats.JsonId"/> for headerless bytes; 0 otherwise. A counted miss that keeps the key: another policy or node reads it.</summary>
    public int ExcludedFormat { get; }

    public bool IsUnreadable => UnreadableFormat != 0;

    public bool IsMiss => IsUnreadable || ExcludedFormat != 0 || Failure is not null;

    public static ReadOutcome<T> Read(T? value, int sourceFormat = 0) => new(value, 0, 0, null, sourceFormat);

    public static ReadOutcome<T> Unreadable(int formatId) => new(default, formatId, 0, null);

    public static ReadOutcome<T> Excluded(int formatId) => new(default, 0, formatId, null);

    public static ReadOutcome<T> Failed(Exception failure) => new(default, 0, 0, failure);
}
