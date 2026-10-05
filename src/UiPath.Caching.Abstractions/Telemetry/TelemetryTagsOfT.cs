using System.Collections;
using System.Runtime.CompilerServices;

namespace UiPath.Caching.Telemetry;

/// <summary>The properties or metrics of one telemetry call: up to eight held inline, more in one array.</summary>
/// <remarks>
/// <para>A struct rather than a <c>ReadOnlySpan&lt;KeyValuePair&lt;string, TValue&gt;&gt;</c> so <see cref="ICachingTelemetryProvider"/>
/// can be mocked: a proxy generator emits invalid IL for a method that takes a span, and an argument matcher cannot
/// read one.</para>
/// <para>A collection expression builds it, <c>[new("key", value)]</c>, and so does a span or an array, implicitly; each is copied.</para>
/// </remarks>
[CollectionBuilder(typeof(TelemetryTagsBuilder), nameof(TelemetryTagsBuilder.Create))]
public readonly struct TelemetryTags<TValue> : IReadOnlyList<KeyValuePair<string, TValue>>, IEquatable<TelemetryTags<TValue>>
{
    private const int InlineCapacity = 8;

    private readonly InlineTags _inline;
    private readonly KeyValuePair<string, TValue>[]? _overflow;

    public TelemetryTags(ReadOnlySpan<KeyValuePair<string, TValue>> tags)
    {
        Count = tags.Length;
        if (tags.Length > InlineCapacity)
        {
            _overflow = tags.ToArray();
            return;
        }

        var inline = default(InlineTags);
        tags.CopyTo(inline);
        _inline = inline;
    }

    public int Count { get; }

    public bool IsEmpty => Count == 0;

    public KeyValuePair<string, TValue> this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return _overflow is null ? _inline[index] : _overflow[index];
        }
    }

    public static implicit operator TelemetryTags<TValue>(ReadOnlySpan<KeyValuePair<string, TValue>> tags) => new(tags);

    public static implicit operator TelemetryTags<TValue>(Span<KeyValuePair<string, TValue>> tags) => new(tags);

    public static implicit operator TelemetryTags<TValue>(KeyValuePair<string, TValue>[]? tags) => new(tags);

    public static bool operator ==(TelemetryTags<TValue> left, TelemetryTags<TValue> right) => left.Equals(right);

    public static bool operator !=(TelemetryTags<TValue> left, TelemetryTags<TValue> right) => !left.Equals(right);

    public Enumerator GetEnumerator() => new(this);

    /// <summary>Equal when both hold the same pairs in the same order; a mock matches arguments with it.</summary>
    /// <remarks>The inherited <see cref="ValueType.Equals(object)"/> throws on the inline buffer.</remarks>
    public bool Equals(TelemetryTags<TValue> other)
    {
        if (Count != other.Count)
        {
            return false;
        }

        for (var i = 0; i < Count; i++)
        {
            var (key, value) = this[i];
            var (otherKey, otherValue) = other[i];
            if (!string.Equals(key, otherKey, StringComparison.Ordinal) || !EqualityComparer<TValue>.Default.Equals(value, otherValue))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is TelemetryTags<TValue> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Count);
        for (var i = 0; i < Count; i++)
        {
            var (key, value) = this[i];
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    IEnumerator<KeyValuePair<string, TValue>> IEnumerable<KeyValuePair<string, TValue>>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<KeyValuePair<string, TValue>>
    {
        private readonly TelemetryTags<TValue> _tags;
        private int _index;

        internal Enumerator(TelemetryTags<TValue> tags)
        {
            _tags = tags;
            _index = -1;
        }

        public readonly KeyValuePair<string, TValue> Current => _tags[_index];

        readonly object IEnumerator.Current => Current;

        public bool MoveNext() => ++_index < _tags.Count;

        public void Reset() => _index = -1;

        public readonly void Dispose()
        {
        }
    }

    [InlineArray(InlineCapacity)]
    private struct InlineTags
    {
        private KeyValuePair<string, TValue> _element;
    }
}
