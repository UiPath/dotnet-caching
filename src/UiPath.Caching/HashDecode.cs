namespace UiPath.Caching;

/// <summary>What <see cref="IByteHashCodecCache.DecodeFields{T}"/> made of the stored fields of a hash.</summary>
/// <param name="Outcome">Found as a read would find a hit, Absent as it would miss, Unreadable when a field or the metadata cannot be read.</param>
/// <param name="Values">The user fields; a field stored empty or without a value reads as <c>default</c>. Empty unless <see cref="SnapshotOutcome.Found"/> or <see cref="SnapshotOutcome.Absent"/>.</param>
/// <param name="Metadata">The user metadata the hash holds, or null.</param>
/// <param name="EmptyMarker">The hash holds the empty metadata field that stands for an empty hash while nulls are cached.</param>
/// <param name="Version">The write count the hash holds, or 0 when it holds none.</param>
/// <param name="Length">The number of stored fields, system fields included, which is the <c>HLEN</c> of the hash.</param>
internal readonly record struct HashDecode<T>(SnapshotOutcome Outcome, IReadOnlyDictionary<string, T?> Values, IDictionary<string, string?>? Metadata, bool EmptyMarker, long Version, int Length)
{
    public static HashDecode<T> Unreadable(int length) => new(SnapshotOutcome.Unreadable, new Dictionary<string, T?>(), null, false, 0, length);
}
