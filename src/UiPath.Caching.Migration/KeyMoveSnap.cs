namespace UiPath.Caching.Migration;

/// <summary>One string read of a primary: the bytes and the remaining life in milliseconds (-1 for none), taken together.</summary>
internal readonly record struct KeyMoveSnap(ReadOutcome Kind, byte[]? Bytes, long TtlMs, DateTimeOffset At)
{
    public static KeyMoveSnap Of(ReadOutcome kind) => new(kind, null, -1, default);
}
