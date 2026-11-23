using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>State comes from the Dst. Control evidence can survive on the Src while the Dst is unavailable.</summary>
internal sealed class TwoServerStateReader(StateStore dstStore, ControlSampler dstSampler, SrcRecordReader srcRecord) : IStateReader
{
    public Task<StateRecord?> ReadAsync(string group, CancellationToken cancellationToken) => dstStore.ReadAsync(group, cancellationToken);

    public Task<ControlSample?> ProbeAsync(string group, CancellationToken cancellationToken) =>
        MergedAsync(group, dstSampler.ProbeAsync, cancellationToken);

    public Task<ControlSample?> SampleAsync(string group, CancellationToken cancellationToken) =>
        MergedAsync(group, dstSampler.SampleAsync, cancellationToken);

    private async Task<ControlSample?> MergedAsync(string group, Func<string, CancellationToken, Task<ControlSample?>> dst, CancellationToken cancellationToken)
    {
        var src = await srcRecord.ReadAsync(group, cancellationToken).ConfigureAwait(false);
        ControlSample? dstSample;
        try
        {
            dstSample = await dst(group, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return src;
        }

        return SampleMerge.Highest(src, dstSample);
    }
}
