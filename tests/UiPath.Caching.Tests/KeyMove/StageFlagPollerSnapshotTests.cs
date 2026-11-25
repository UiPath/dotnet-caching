using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

// Start takes a snapshot of its groups and of the token it stops with.
public class StageFlagPollerSnapshotTests
{
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task A_group_added_to_the_callers_list_after_Start_is_not_polled_and_the_list_is_read_once()
    {
        var source = new CountingSource();
        var poller = new StageFlagPoller(source, _ => true, new KeyMoveStateOptions(), _time);
        var groups = new CountingEnumerable(["g1"]);

        poller.Start(groups, CancellationToken.None);
        groups.Items.Add("g2");
        await _time.WaitForTimersAsync(() => _time.TimersCreated >= 2 && _time.TimerCount == 1);
        poller.ViewOf("g1").Should().Be(3);
        var created = _time.TimersCreated;
        _time.Advance(TimeSpan.FromSeconds(2));
        await _time.WaitForTimersAsync(() => _time.TimersCreated >= created + 2 && _time.TimerCount == 1);
        source.Reads("g1").Should().Be(2);

        await poller.StopAsync();

        source.Reads("g2").Should().Be(0);
        groups.Enumerations.Should().Be(1);
    }

    [Fact]
    public async Task A_second_Start_before_Stop_is_refused_and_a_stopped_poller_starts_again()
    {
        var poller = new StageFlagPoller(new CountingSource(), _ => true, new KeyMoveStateOptions(), _time);
        poller.Start(["g1"], CancellationToken.None);

        var again = () => poller.Start(["g1"], CancellationToken.None);

        again.Should().Throw<InvalidOperationException>();
        await poller.StopAsync();
        poller.Start(["g1"], CancellationToken.None);
        await poller.StopAsync();
    }

    private sealed class CountingEnumerable(List<string> items) : IEnumerable<string>
    {
        public List<string> Items { get; } = items;

        public int Enumerations { get; private set; }

        public IEnumerator<string> GetEnumerator()
        {
            Enumerations++;
            return Items.ToList().GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountingSource : IMigrationStageSource
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _reads = new();

        public int Reads(string group) => _reads.GetValueOrDefault(group);

        public ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken)
        {
            _reads.AddOrUpdate(group, 1, (_, n) => n + 1);
            return ValueTask.FromResult<int?>(3);
        }
    }
}
