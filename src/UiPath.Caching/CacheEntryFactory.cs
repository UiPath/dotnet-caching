namespace UiPath.Caching;

public sealed class CacheEntryFactory : ICacheEntryFactory
{
    public ICacheEntry<T> Create<T>(T value, DateTimeOffset expiration, IDictionary<string, string?>? properties = null) =>
        new CacheEntry<T>(value, expiration, properties);

    private struct Marks : IEquatable<Marks>
    {
        public int Read;
        public long ReplacedBy;
        public long Replaces;

        public readonly bool Equals(Marks other) => true;

        public override readonly bool Equals(object? obj) => obj is Marks;

        public override readonly int GetHashCode() => 0;
    }

    private sealed record CacheEntry<T> : ICacheEntry<T>, IReadTracked, IRefreshLinked
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

        int IReadTracked.LastRead => Volatile.Read(ref _marks.Read);

        long IRefreshLinked.ReplacedBy
        {
            get => Volatile.Read(ref _marks.ReplacedBy);
            set => Volatile.Write(ref _marks.ReplacedBy, value);
        }

        long IRefreshLinked.Replaces
        {
            get => Volatile.Read(ref _marks.Replaces);
            set => Volatile.Write(ref _marks.Replaces, value);
        }

        void IReadTracked.MarkRead(int nowSeconds)
        {
            if (Volatile.Read(ref _marks.Read) != nowSeconds)
            {
                Volatile.Write(ref _marks.Read, nowSeconds);
            }
        }

        public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null) =>
            new CacheEntry<T>(Value, expiration, metadata);
    }
}
