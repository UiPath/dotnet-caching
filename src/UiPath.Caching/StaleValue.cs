namespace UiPath.Caching;

/// <summary>One kept value: the entry, the size the local tier charged it, when its local copy expired, the end of the stale age it was written under, the store's epoch when it was kept, and the end of the cooldown after fail-safe last returned it.</summary>
internal sealed class StaleValue(ICacheEntry entry, long size, DateTimeOffset localDeadline, DateTimeOffset until, long epoch)
{
    private long _cooldownUntil = DateTimeOffset.MinValue.UtcTicks;

    public ICacheEntry Entry { get; } = entry;

    public long Size { get; } = size;

    public DateTimeOffset Until { get; } = until;

    public long Epoch { get; } = epoch;

    /// <summary>Whether a caller that allows <paramref name="maxStaleAge"/> may be answered with the value: the writer's stale age bounds how long it is kept, each caller's own what it is served.</summary>
    public bool Within(DateTimeOffset now, TimeSpan maxStaleAge) =>
        now < Until && now.UtcTicks - localDeadline.UtcTicks < maxStaleAge.Ticks;

    public bool Cooling(DateTimeOffset now) => now.UtcTicks < Volatile.Read(ref _cooldownUntil);

    public void Cool(DateTimeOffset now, TimeSpan cooldown)
    {
        var ticks = cooldown.Ticks > DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks ? DateTimeOffset.MaxValue.UtcTicks : now.UtcTicks + cooldown.Ticks;
        Volatile.Write(ref _cooldownUntil, ticks);
    }
}
