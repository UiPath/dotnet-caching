namespace UiPath.Caching.Migration;

/// <summary>The keys of one cache that a dry run met.</summary>
internal sealed class CacheInventory
{
    /// <summary>The upper bounds of the size buckets, in bytes; a value at or above the last falls in a fifth bucket.</summary>
    public static readonly IReadOnlyList<long> BucketLimits = [1024, 16 * 1024, 256 * 1024, 1024 * 1024];

    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly long[] _sizes = new long[BucketLimits.Count + 1];
    private long _movableSourceBytes;

    public IReadOnlyDictionary<string, long> Counts => _counts;

    /// <summary>Keys per size bucket of the value on the in-place side: under 1 KiB, 16 KiB, 256 KiB, 1 MiB, and the rest.</summary>
    public IReadOnlyList<long> Sizes => _sizes;

    public long SourceBytes { get; private set; }

    public long Probed { get; private set; }

    public long Decoded { get; private set; }

    public long EncodedBytes { get; private set; }

    /// <summary>The movable keys whose encoded length is in <see cref="EncodedBytes"/>.</summary>
    public long EncodedKeys { get; private set; }

    /// <summary>Some key was selected for classification without decoding.</summary>
    public bool Sampled { get; private set; }

    public long Movable => _counts.GetValueOrDefault(DryRunClasses.Movable);

    /// <summary>The mean encoded length of the sample times the movable keys; the movable source bytes when there is no encoded sample.</summary>
    public long EstimatedNativeBytes => EncodedKeys == 0 ? _movableSourceBytes : (long)((double)EncodedBytes / EncodedKeys * Movable);

    /// <summary>Whether the next probed key is decoded: every key up to the cap, then one in every <see cref="DryRunOptions.SampleEvery"/>.</summary>
    public bool ShouldDecode(DryRunOptions options) =>
        Probed < options.DecodeCap || (Probed + 1 - options.DecodeCap) % options.SampleEvery == 0;

    public CacheInventory Clone()
    {
        var copy = new CacheInventory { SourceBytes = SourceBytes, Decoded = Decoded, EncodedBytes = EncodedBytes, EncodedKeys = EncodedKeys, Sampled = Sampled, Probed = Probed, _movableSourceBytes = _movableSourceBytes };
        foreach (var (name, count) in _counts)
        {
            copy._counts[name] = count;
        }

        _sizes.CopyTo(copy._sizes, 0);
        return copy;
    }

    /// <summary>Counts a key that was never probed.</summary>
    public void Count(string name) => _counts[name] = _counts.GetValueOrDefault(name) + 1;

    public void Add(DryRunProbe probe, bool decodeRequested)
    {
        Probed++;
        Count(probe.Class);
        if (probe.SourceBytes is { } bytes)
        {
            SourceBytes += bytes;
            _sizes[BucketOf(bytes)]++;
            if (probe.Class == DryRunClasses.Movable)
            {
                _movableSourceBytes += bytes;
            }
        }

        if (probe.Decoded)
        {
            Decoded++;
        }
        Sampled |= !decodeRequested;

        if (probe.EncodedBytes is { } encoded)
        {
            EncodedBytes += encoded;
            EncodedKeys++;
        }
    }

    private static int BucketOf(long bytes)
    {
        var bucket = 0;
        while (bucket < BucketLimits.Count && bytes >= BucketLimits[bucket])
        {
            bucket++;
        }

        return bucket;
    }
}
