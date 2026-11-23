namespace UiPath.Caching.KeyMove;

// Retry a combined read when either connection changes during it.
internal sealed class InstanceRecordReader(ControlRecordReader src, ControlRecordReader dst, ConnectionChanges? changes = null) : IRecordReader
{
    public const int Attempts = 3;

    private readonly RecordSide[] _sides = [RecordSide.Src, RecordSide.Dst];

    public async Task<IReadOnlyList<(RecordSide Side, int Slot, IReadOnlyDictionary<string, string> Fields)>> ReadAllAsync(string group, CancellationToken cancellationToken)
    {
        var attempt = 1;
        while (true)
        {
            var (srcMark, dstMark) = (changes?.SourceCurrent ?? 0, changes?.DestinationCurrent ?? 0);
            var reads = await Task.WhenAll(src.ReadAllAsync(group, cancellationToken), dst.ReadAllAsync(group, cancellationToken)).ConfigureAwait(false);
            var all = new List<(RecordSide, int, IReadOnlyDictionary<string, string>)>();
            for (var i = 0; i < _sides.Length; i++)
            {
                all.AddRange(reads[i].Select(r => (_sides[i], r.Slot, r.Fields)));
            }

            if (changes is null || attempt == Attempts || (!changes.ChangedSince(RecordSide.Src, srcMark) && !changes.ChangedSince(RecordSide.Dst, dstMark)))
            {
                return all;
            }

            attempt++;
        }
    }

    public Task<IReadOnlyDictionary<string, string>> ReadSampledAsync(string group, ControlSample sample, CancellationToken cancellationToken) =>
        ((IRecordReader)(sample.SrcHome ? src : dst)).ReadSampledAsync(group, sample, cancellationToken);
}
