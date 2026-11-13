using StackExchange.Redis;
using UiPath.Caching.Negotiation;

namespace UiPath.Caching.Tests.Negotiation;

public class StreamNodeListTests
{
    private static readonly RedisKey Stream = "app:st:orders";

    private readonly FakeGroups _groups = new();
    private readonly FormatNegotiationOptions _options = new();

    [Fact]
    public async Task Group_with_fresh_ad_is_accounted()
    {
        _groups.Add(Stream, "node-a", TimeSpan.FromSeconds(1));

        var blockers = await new StreamNodeList(_groups, _options).BlockersAsync([Stream], Ads("node-a"), TestContext.Current.CancellationToken);

        blockers.Should().BeEmpty();
    }

    [Fact]
    public async Task Group_read_after_its_ad_was_written_is_a_restart_under_the_same_id_and_blocks()
    {
        _groups.Add(Stream, "node-a", TimeSpan.FromSeconds(1));
        var tooOld = TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(_options.AdvertiseInterval.Ticks * 2) + TimeSpan.FromSeconds(1);

        var blockers = await new StreamNodeList(_groups, _options).BlockersAsync([Stream], new Dictionary<string, TimeSpan> { ["node-a"] = tooOld }, TestContext.Current.CancellationToken);

        blockers.Should().Equal("node-a");
    }

    [Fact]
    public async Task Group_without_ad_and_low_idle_blocks_and_names_the_node()
    {
        _groups.Add(Stream, "node-a", TimeSpan.FromSeconds(1));
        _groups.Add(Stream, "legacy-1", TimeSpan.FromSeconds(30));

        var blockers = await new StreamNodeList(_groups, _options).BlockersAsync([Stream], Ads("node-a"), TestContext.Current.CancellationToken);

        blockers.Should().Equal("legacy-1");
    }

    [Fact]
    public async Task Group_with_high_idle_is_dead_and_does_not_block()
    {
        _groups.Add(Stream, "gone", _options.LivenessWindow + TimeSpan.FromSeconds(1));
        _groups.Add(Stream, "edge", _options.LivenessWindow);

        var blockers = await new StreamNodeList(_groups, _options).BlockersAsync([Stream], Ads(), TestContext.Current.CancellationToken);

        blockers.Should().Equal("edge");
    }

    [Fact]
    public async Task Group_without_a_consumer_is_nobody()
    {
        _groups.Add(Stream, "joining", idle: null);

        (await new StreamNodeList(_groups, _options).BlockersAsync([Stream], Ads(), TestContext.Current.CancellationToken)).Should().BeEmpty("no consumer ever read from it, so no running node owns it");
    }

    [Fact]
    public async Task A_group_on_two_streams_is_named_once_and_ids_match_regardless_of_case()
    {
        RedisKey other = "app:st:users";
        _groups.Add(Stream, "legacy", TimeSpan.Zero);
        _groups.Add(other, "legacy", TimeSpan.Zero);
        _groups.Add(other, "Node-A", TimeSpan.Zero);

        var blockers = await new StreamNodeList(_groups, _options).BlockersAsync([Stream, other], Ads("node-a"), TestContext.Current.CancellationToken);

        blockers.Should().Equal("legacy");
    }

    private static Dictionary<string, TimeSpan> Ads(params string[] nodes) => nodes.ToDictionary(n => n, _ => TimeSpan.Zero);

    private sealed class FakeGroups : IStreamGroupSource
    {
        private readonly Dictionary<(string, string), TimeSpan?> _idle = [];
        private readonly Dictionary<string, List<string>> _names = [];

        public List<string> IdleQueries { get; } = [];

        public void Add(RedisKey stream, string group, TimeSpan? idle)
        {
            _idle[(stream.ToString(), group)] = idle;
            if (!_names.TryGetValue(stream.ToString(), out var names))
            {
                _names[stream.ToString()] = names = [];
            }

            names.Add(group);
        }

        public Task<IReadOnlyList<string>> GroupsAsync(RedisKey stream, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(_names.TryGetValue(stream.ToString(), out var names) ? names : []);

        public Task<TimeSpan?> IdleAsync(RedisKey stream, string group, CancellationToken cancellationToken)
        {
            IdleQueries.Add(group);
            return Task.FromResult(_idle[(stream.ToString(), group)]);
        }
    }
}
