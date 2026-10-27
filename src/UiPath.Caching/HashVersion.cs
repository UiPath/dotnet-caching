namespace UiPath.Caching;

/// <summary>What a local entry is the state of: the hash at <see cref="Number"/> with this node's <see cref="Own"/> writes laid over it.</summary>
internal abstract class HashVersion(long number, OwnWrite[] own, DateTimeOffset? localDeadline)
{
    /// <summary>The low bits of a version count the writes of one epoch of the hash; the bits above name it.</summary>
    internal const int CounterBits = 24;

    private const int EpochBits = 38;

    /// <summary>Zero while the entry waits on the replace that made it.</summary>
    public long Number { get; } = number;

    public OwnWrite[] Own { get; } = own;

    /// <summary>When the local cap on the entry's lifetime ends; null when it has none.</summary>
    public DateTimeOffset? LocalDeadline { get; } = localDeadline;

    // A hash made again after a removal, an expiry or an eviction counts from one, so a late event of the old hash can look like the next write of the new one; the epoch tells them apart.
    // The seed is below 2^62, which leaves the count room to grow without reaching long.MaxValue.
    internal static long NewEpochSeed() =>
        Random.Shared.NextInt64(1, 1L << EpochBits) << CounterBits;

    /// <summary>True when both versions are of one epoch of the hash, which no removal, expiry or eviction ended; false when either carries none, as the count of a node that draws none.</summary>
    internal static bool SameEpoch(long a, long b) =>
        a >> CounterBits != 0 && a >> CounterBits == b >> CounterBits;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3267:Loops should be simplified with LINQ expressions", Justification = "LINQ would allocate a closure on the write path.")]
    internal static bool ReturnedAtOrBefore(OwnWrite[] own, long version)
    {
        foreach (var write in own)
        {
            if (write.Version != 0 && write.Version <= version)
            {
                return true;
            }
        }

        return false;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3267:Loops should be simplified with LINQ expressions", Justification = "LINQ would allocate a closure on the write path.")]
    internal static bool Writes(OwnWrite[] own, string field)
    {
        foreach (var write in own)
        {
            if (Array.IndexOf(write.Keys, field) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>False when the entry is no longer the state of any version; otherwise the version it stands on once the own writes that returned are counted, and those still ahead of it.</summary>
    internal bool TryResolve(out long number, out OwnWrite[] ahead)
    {
        number = Number;
        ahead = Own;
        var i = 0;
        for (; i < Own.Length; i++)
        {
            var write = Own[i];
            if (write.Failed)
            {
                return false;
            }

            if (write.Version == 0)
            {
                break;
            }

            if (write.Replace)
            {
                if (number != 0)
                {
                    return false;
                }

                number = write.Version;
            }
            else if (number == 0 || write.Version <= number || number >> CounterBits != write.Version >> CounterBits)
            {
                return false;
            }
            else if (write.Version == number + 1)
            {
                number = write.Version;
            }
            else
            {
                // Writes of other nodes came between: their events, taken in order, bring the entry up to this write.
                break;
            }
        }

        ahead = i == 0 ? Own : Own[i..];
        return true;
    }

    internal abstract bool Apply(MultilayerHashCache cache, RefreshMetadataState state, object current, PeerUpdate update);
}
