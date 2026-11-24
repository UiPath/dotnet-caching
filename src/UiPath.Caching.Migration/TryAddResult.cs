namespace UiPath.Caching.Migration;

/// <summary>Created: this call made the key. Exists: a value was there, returned when it was read. Failed: not sent or unknown, as typed.</summary>
internal readonly record struct TryAddResult<T>(TryAddKind Kind, T? Existing, WriteResult Failure)
    where T : class
{
    public static TryAddResult<T> Created => new(TryAddKind.Created, null, default);

    public static TryAddResult<T> Rerun => new(TryAddKind.Rerun, null, default);

    public static TryAddResult<T> Helped => new(TryAddKind.Helped, null, default);

    public static TryAddResult<T> Exists(T? existing) => new(TryAddKind.Exists, existing, default);

    public static TryAddResult<T> Failed(WriteResult failure) => new(TryAddKind.Failed, null, failure);
}
