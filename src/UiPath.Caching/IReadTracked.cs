namespace UiPath.Caching;

/// <summary>An entry that remembers, to the second, when a local hit last read it.</summary>
internal interface IReadTracked
{
    const int NeverRead = 0;

    /// <summary>The second of the last read on the cache's monotonic clock, at least 1; <see cref="NeverRead"/> when none has read it.</summary>
    int LastRead { get; }

    /// <summary>Records a read at <paramref name="nowSeconds"/>; writes only when the second changed, so hits on a hot entry share its cache line.</summary>
    void MarkRead(int nowSeconds);
}
