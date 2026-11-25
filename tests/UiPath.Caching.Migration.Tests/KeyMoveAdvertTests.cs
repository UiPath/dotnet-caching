using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

// The key move tokens ride in the router's entry and the leader reads them back from the node hash, fresh by the server's clock.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveAdvertTests(RedisFixture fixture) : IAsyncLifetime
{
    private const string Group = "orders";

    private readonly RedisKey _key = $"km-adv-test:{Guid.NewGuid():N}";
    private ConnectionMultiplexer? _redis;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Db => _redis!.GetDatabase();

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_redis is not null)
        {
            await Db.KeyDeleteAsync(_key);
            await _redis.DisposeAsync();
        }
    }

    [Fact]
    public void The_tokens_survive_the_router_entry_encoding_and_parse_as_the_leader_reads_them()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var node = new KeyMoveAdvertTokens(
            [new GroupRegistration(Group, false, true)],
            _ => ["b", "a"],
            new StageFlagPoller(new Source(4), _ => true, new KeyMoveStateOptions(), clock),
            new Acks(new AckedIdentity(7, "ab12", "S3", "fwd")),
            new EpochGate(),
            new GroupStateMonitor(new NoReader(), new KeyMoveStateOptions(), clock));
        var tokens = new List<string>();
        new KeyMoveTokens(node).AddTo(tokens);

        var text = new RouterAdvertisement("urn:a", "b1", tokens).Encode();

        RouterAdvertisement.TryParse(text, out var parsed).Should().BeTrue();
        parsed!.Tokens.Should().Equal(tokens);
        var advert = new NodeAdvert("urn:a#router", TimeSpan.Zero, parsed.Tokens);
        advert.Should().Match<NodeAdvert>(a => a.HasKm && a.Lists(Group));
        advert.CachesOf(Group).Should().Equal("a", "b");
        advert.IdentityOf(Group).Should().Be(new AdvertIdentity(7, "ab12", "S3", "fwd"));
    }

    [Fact]
    public async Task The_source_returns_the_router_entries_with_their_server_age_and_leaves_out_library_entries_and_routers_that_left()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.");
        var store = new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), _key);
        await store.WriteAsync("urn:a#router", new RouterAdvertisement("urn:a", "b1", ["km", $"kmg={Group}", $"fl={Group}:3", $"{Group}@0"]), Ct);
        await store.WriteAsync("urn:b#router", new RouterAdvertisement("urn:b", "b1", ["km"], Left: true), Ct);
        await Db.HashSetAsync(_key, "urn:lib", "1|lib=1;inst=x;vr=json");

        var adverts = await new RouterAdvertSource(store).ReadAsync(Ct);

        var only = adverts.Should().ContainSingle().Subject;
        only.Node.Should().Be("urn:a#router");
        only.Age.Should().BeLessThan(TimeSpan.FromSeconds(5));
        only.FlagOf(Group).Should().Be(3);
        only.Lists(Group).Should().BeTrue();
        only.IdentityOf(Group).Should().BeNull();
    }

    private sealed class Source(int value) : IMigrationStageSource
    {
        public ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken) => ValueTask.FromResult<int?>(value);
    }

    private sealed class Acks(AckedIdentity identity) : IAckedIdentitySource
    {
        public AckedIdentity? AckedFor(string group) => identity;
    }

    private sealed class NoReader : IStateReader
    {
        public Task<StateRecord?> ReadAsync(string group, CancellationToken cancellationToken) => Task.FromResult<StateRecord?>(null);

        public Task<ControlSample?> ProbeAsync(string group, CancellationToken cancellationToken) => Task.FromResult<ControlSample?>(null);

        public Task<ControlSample?> SampleAsync(string group, CancellationToken cancellationToken) => Task.FromResult<ControlSample?>(null);
    }
}
