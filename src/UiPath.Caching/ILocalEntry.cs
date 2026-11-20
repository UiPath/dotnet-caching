namespace UiPath.Caching;

/// <summary>The local-tier bookkeeping of a cache entry: read mark, refresh links and local deadline.</summary>
internal interface ILocalEntry
{
    const int NeverRead = 0;

    const long NoRefresh = 0;

    /// <summary>The second of the last read on the cache's monotonic clock, at least 1; <see cref="NeverRead"/> when none has read it.</summary>
    int LastRead { get; }

    /// <summary>False when nothing consults the read mark, so a hit does not stamp it.</summary>
    bool IsTracked { get; }

    /// <summary>The number of the refresh that replaced this entry; <see cref="NoRefresh"/> until one has. Refreshes link by number, not by reference, so neither entry keeps the other alive.</summary>
    long ReplacedBy { get; set; }

    /// <summary>The number of the refresh that kept this entry in place of another; <see cref="NoRefresh"/> when a local write or read stored it.</summary>
    long Replaces { get; set; }

    /// <summary>The deadline of the last local write of the entry; <see cref="DateTimeOffset.MinValue"/> when none.</summary>
    DateTimeOffset LocalDeadline { get; set; }

    /// <summary>Sets whether the entry takes read marks; set before the entry is published.</summary>
    void Track(bool tracked);

    /// <summary>Records a read unless the entry is untracked; writes only when the second changed, so hits on a hot entry share its cache line.</summary>
    void MarkRead(int nowSeconds);
}
