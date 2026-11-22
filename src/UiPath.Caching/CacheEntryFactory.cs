namespace UiPath.Caching;

public sealed class CacheEntryFactory : ICacheEntryFactory
{
    private const int Untracked = -1;

    public ICacheEntry<T> Create<T>(T value, DateTimeOffset expiration, IDictionary<string, string?>? properties = null) =>
        new CacheEntry<T>(value, expiration, properties);

    private struct Marks : IEquatable<Marks>
    {
        public int Read;
        public long ReplacedBy;
        public long Replaces;
        public long LocalDeadline;
        public int TokenChanged;

        public readonly bool Equals(Marks other) => true;

        public override readonly bool Equals(object? obj) => obj is Marks;

        public override readonly int GetHashCode() => 0;
    }

    private sealed record CacheEntry<T> : ICacheEntry<T>, ILocalEntry
    {
        // Not part of the entry's value: every copy of an entry equals the others.
        private Marks _marks;

        public CacheEntry(T? value, DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null)
        {
            Value = value;
            Expiration = expiration ?? DateTimeOffset.MaxValue;
            Metadata = metadata;
        }

        public T? Value { get; private set; }

        public DateTimeOffset Expiration { get; private set; }

        public IDictionary<string, string?>? Metadata { get; private set; }

        object? ICacheEntry.Value => Value;

        int ILocalEntry.LastRead => Math.Max(Volatile.Read(ref _marks.Read), ILocalEntry.NeverRead);

        bool ILocalEntry.IsTracked => Volatile.Read(ref _marks.Read) != Untracked;

        long ILocalEntry.ReplacedBy
        {
            get => Volatile.Read(ref _marks.ReplacedBy);
            set => Volatile.Write(ref _marks.ReplacedBy, value);
        }

        long ILocalEntry.Replaces
        {
            get => Volatile.Read(ref _marks.Replaces);
            set => Volatile.Write(ref _marks.Replaces, value);
        }

        DateTimeOffset ILocalEntry.LocalDeadline
        {
            get => Volatile.Read(ref _marks.LocalDeadline) is var ticks and > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : DateTimeOffset.MinValue;
            set => Volatile.Write(ref _marks.LocalDeadline, value.UtcTicks);
        }

        bool ILocalEntry.TokenChanged => Volatile.Read(ref _marks.TokenChanged) == 1;

        void ILocalEntry.MarkTokenChanged() => Volatile.Write(ref _marks.TokenChanged, 1);

        void ILocalEntry.Track(bool tracked)
        {
            if (!tracked)
            {
                Volatile.Write(ref _marks.Read, Untracked);
            }
            else if (Volatile.Read(ref _marks.Read) == Untracked)
            {
                Volatile.Write(ref _marks.Read, ILocalEntry.NeverRead);
            }
        }

        void ILocalEntry.MarkRead(int nowSeconds)
        {
            var last = Volatile.Read(ref _marks.Read);
            if (last != nowSeconds && last != Untracked)
            {
                Volatile.Write(ref _marks.Read, nowSeconds);
            }
        }

        public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null) =>
            new CacheEntry<T>(Value, expiration, metadata);
    }
}
