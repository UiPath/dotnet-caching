using StackExchange.Redis;

namespace UiPath.Caching.Migration;

/// <summary>One read of a hash on a primary: its stored fields and its remaining life in milliseconds (-1 for none), taken together.</summary>
/// <param name="Entries">The stored fields as the server holds them, system fields included; null unless the hash was read.</param>
/// <param name="Length">The <c>HLEN</c> when it was asked, otherwise the number of fields read.</param>
/// <param name="TooLarge">The field count is above the cap, so the hash was not read.</param>
internal readonly record struct HashSnap(ReadOutcome Kind, HashEntry[]? Entries, long TtlMs, DateTimeOffset At, long Length = 0, bool TooLarge = false)
{
    public static HashSnap Of(ReadOutcome kind) => new(kind, null, -1, default);

    /// <summary>The stored fields as the codec takes them.</summary>
    public KeyValuePair<string, byte[]>[] Stored() =>
        [.. (Entries ?? []).Select(e => new KeyValuePair<string, byte[]>(e.Name.ToString(), ((byte[]?)e.Value)!))];
}
