namespace UiPath.Caching;

public sealed class CacheEntryFactory : ICacheEntryFactory
{
    public ICacheEntry<T> Create<T>(T value, DateTimeOffset expiration, IDictionary<string, string?>? properties = null) =>
        new CacheEntry<T>(value, expiration, properties);

    private struct ReadMark : IEquatable<ReadMark>
    {
        public int Value;

        public readonly bool Equals(ReadMark other) => true;

        public override readonly bool Equals(object? obj) => obj is ReadMark;

        public override readonly int GetHashCode() => 0;
    }

    private sealed record CacheEntry<T> : ICacheEntry<T>, IReadTracked
    {
        // Not part of the entry's value: every copy of an entry equals the others.
        private ReadMark _read;

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

        int IReadTracked.LastRead => Volatile.Read(ref _read.Value);

        void IReadTracked.MarkRead(int nowSeconds)
        {
            if (Volatile.Read(ref _read.Value) != nowSeconds)
            {
                Volatile.Write(ref _read.Value, nowSeconds);
            }
        }

        public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null) =>
            new CacheEntry<T>(Value, expiration, metadata);
    }
}
