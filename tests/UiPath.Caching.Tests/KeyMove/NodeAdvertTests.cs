using UiPath.Caching.KeyMove;
using static UiPath.Caching.Tests.KeyMove.PlannerKit;

namespace UiPath.Caching.Tests.KeyMove;

// The token grammar as the node writes it and the leader reads it.
public class NodeAdvertTests
{
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);
    private readonly FakeStateReader _reader = new();

    [Fact]
    public void The_identity_token_round_trips_with_its_state_and_direction_and_a_node_with_no_ack_advertises_at_0()
    {
        var token = NodeAdvert.Identity(Group, new AckedIdentity(12, "ab", "S3", "rev"));
        var advert = new NodeAdvert("n", TimeSpan.Zero, [token]);

        token.Should().Be("g1@12:ab:S3r");
        advert.IdentityOf(Group).Should().Be(new AdvertIdentity(12, "ab", "S3", "rev"));
        NodeAdvert.Identity(Group, null).Should().Be("g1@0");
        new NodeAdvert("n", TimeSpan.Zero, ["g1@0"]).IdentityOf(Group).Should().BeNull();
        new NodeAdvert("n", TimeSpan.Zero, []).IdentityOf(Group).Should().BeNull();
    }

    [Fact]
    public void An_ack_is_the_exact_ep_and_id_of_the_state_and_malformed_tokens_are_no_ack()
    {
        var state = State("S2", ep: 7);

        Advert("n", acks: state).Acknowledges(Group, state).Should().BeTrue();
        Advert("n", acks: state).Acknowledges(Group, state with { Id = "other" }).Should().BeFalse();
        new NodeAdvert("n", TimeSpan.Zero, ["g1@x:y"]).IdentityOf(Group).Should().BeNull();
        new NodeAdvert("n", TimeSpan.Zero, ["g1@7:id7:S"]).IdentityOf(Group).Should().BeNull();
    }

    [Fact]
    public void The_other_tokens_are_read_per_group_and_a_group_whose_name_prefixes_another_is_not_confused()
    {
        var advert = new NodeAdvert("n", TimeSpan.Zero, ["km", "kmg=g1", "pin=g1", "ca=g1:b,a", "fl=g1:4", "pi=g1:2", "rc=g1:9", "ctlq", "byp", "snap=3", "ap=app", "fl=g10:1"]);

        advert.Should().Match<NodeAdvert>(a => a.HasKm && a.Lists("g1") && a.Pinned("g1") && a.FlagOf("g1") == 4 && a.PromotionUnits("g1") == 2 && a.Recycled("g1") == 9 && a.Suspects && a.Bypasses && a.SnapshotSeconds == 3 && a.Scope == "app");
        advert.CachesOf("g1").Should().Equal("a", "b");
        advert.FlagOf("g10").Should().Be(1);
        advert.FlagOf("g").Should().BeNull();
        advert.Lists("g2").Should().BeFalse();
        advert.CachesOf("g2").Should().BeNull();
    }

    [Fact]
    public void A_group_name_that_holds_a_colon_is_read_with_its_own_prefix()
    {
        var advert = new NodeAdvert("n", TimeSpan.Zero, ["fl=a:b:3", "ca=a:b:x,y"]);

        advert.FlagOf("a:b").Should().Be(3);
        advert.CachesOf("a:b").Should().Equal("x", "y");
        advert.FlagOf("a").Should().BeNull();
    }

    [Fact]
    public async Task The_node_always_writes_the_key_move_token_and_per_relevant_group_the_listing_the_caches_the_view_the_ack_and_the_promotion_count()
    {
        var gate = new EpochGate();
        var monitor = new GroupStateMonitor(_reader, new KeyMoveStateOptions(), _time);
        monitor.Register([new GroupRegistration(Group, false, true), new GroupRegistration("quiet", false, false)]);
        _reader.State = FakeStateReader.Record("S3", 6, "ab", nPop: 1);
        await monitor.StepAsync(Group, CancellationToken.None);
        var source = new Flags(4);
        var poller = new StageFlagPoller(source, _ => true, new KeyMoveStateOptions(), _time);
        await poller.StepAsync(Group, CancellationToken.None);
        new AckCoordinator(monitor, gate, new StageDriverOptions(), _time).Tick(Group);
        gate.Enter(Group, promotion: true);
        var tokens = new KeyMoveAdvertTokens([new GroupRegistration(Group, false, true), new GroupRegistration("quiet", false, false)], _ => ["b", "a"], poller, monitor, gate, monitor, _ => 6);

        var list = new List<string>();
        tokens.AddTo(list);

        list.Should().Equal("km", "kmb", "kmg=g1", "ca=g1:a,b", "fl=g1:4", "g1@6:ab:S3f", "pi=g1:1", "rc=g1:6", "snap=0");
    }

    [Fact]
    public void A_pod_that_has_not_read_the_flag_advertises_no_fl_and_one_not_acknowledging_advertises_at_0()
    {
        var gate = new EpochGate();
        var monitor = new GroupStateMonitor(_reader, new KeyMoveStateOptions(), _time);
        monitor.Register([new GroupRegistration(Group, false, true)]);
        var poller = new StageFlagPoller(new Flags(null), _ => true, new KeyMoveStateOptions(), _time);
        var tokens = new KeyMoveAdvertTokens([new GroupRegistration(Group, false, true)], _ => ["a"], poller, monitor, gate, monitor);

        var list = new List<string>();
        tokens.AddTo(list);

        list.Should().Equal("km", "kmb", "kmg=g1", "ca=g1:a", "g1@0", "pi=g1:0");
    }

    [Fact]
    public void A_view_above_5_reads_as_5()
    {
        new NodeAdvert("n", TimeSpan.Zero, ["fl=g1:9"]).FlagOf(Group).Should().Be(5);
    }

    private sealed class Flags(int? value) : IMigrationStageSource
    {
        public ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken) => ValueTask.FromResult(value);
    }
}
