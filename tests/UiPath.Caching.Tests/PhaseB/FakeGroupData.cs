using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

internal sealed class FakeGroupData : IGroupDataOps
{
    public long Native { get; set; }

    public bool PurgeWorks { get; set; } = true;

    public List<string> Calls { get; } = [];

    public Task<long> CountNativeAsync(string group, CancellationToken cancellationToken)
    {
        Calls.Add("count");
        return Task.FromResult(Native);
    }

    public Task PurgeNativeAsync(string group, CancellationToken cancellationToken)
    {
        Calls.Add("purge_n");
        if (PurgeWorks)
        {
            Native = 0;
        }

        return Task.CompletedTask;
    }

    public Task PurgeLegacyAsync(string group, CancellationToken cancellationToken)
    {
        Calls.Add("purge_l");
        return Task.CompletedTask;
    }
}
