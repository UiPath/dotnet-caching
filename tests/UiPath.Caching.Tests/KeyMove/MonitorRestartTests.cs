using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

public class MonitorRestartTests
{
    [Fact]
    public async Task A_stopped_monitor_polls_every_group_again_when_it_is_started_again()
    {
        var reader = new FakeStateReader { State = FakeStateReader.Record("S1", 5, "id1") };
        var monitor = new GroupStateMonitor(reader, new KeyMoveStateOptions(), new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        GroupRegistration[] groups = [new("g1", false, true)];

        monitor.Start(groups, CancellationToken.None);
        await TimeSteps.UntilAsync(() => monitor.RulesFor("g1").Acting?.Id == "id1");
        await monitor.StopAsync();
        reader.State = FakeStateReader.Record("S1", 6, "id2");
        monitor.Start(groups, CancellationToken.None);
        await TimeSteps.UntilAsync(() => monitor.RulesFor("g1").Acting?.Id == "id2");
        await monitor.StopAsync();
    }
}
