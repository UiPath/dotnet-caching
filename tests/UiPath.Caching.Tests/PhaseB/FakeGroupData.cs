using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

internal sealed class FakeGroupData : IGroupDataOps
{
    public long Native { get; set; }

    public bool PurgeWorks { get; set; } = true;

    public List<string> Calls { get; } = [];

    // Runs once, after the first census that found a key: a state that moves, or a lease that is lost, between the census and the purge.
    public Func<Task>? AfterFirstCount { get; set; }

    public List<DataGuard> Guards { get; } = [];

    public async Task<long> CountNativeAsync(string group, CancellationToken cancellationToken)
    {
        Calls.Add("count");
        var found = Native;
        if (found > 0 && AfterFirstCount is { } hook)
        {
            AfterFirstCount = null;
            await hook();
        }

        return found;
    }

    public Task PurgeNativeAsync(string group, DataGuard guard, CancellationToken cancellationToken)
    {
        Calls.Add("purge_n");
        Guards.Add(guard);
        if (PurgeWorks)
        {
            Native = 0;
        }

        return Task.CompletedTask;
    }

    public Task PurgeLegacyAsync(string group, DataGuard guard, CancellationToken cancellationToken)
    {
        Calls.Add("purge_l");
        Guards.Add(guard);
        return Task.CompletedTask;
    }
}
