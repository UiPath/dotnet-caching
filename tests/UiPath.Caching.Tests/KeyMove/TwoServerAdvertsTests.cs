using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The two servers' node hashes merge by node id, each advert aged on its own server's clock.</summary>
public sealed class TwoServerAdvertsTests
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(6);
    private static readonly AckedIdentity Acked = new(5, "idx", "S1", "fwd");

    [Fact]
    public async Task A_node_fresh_on_both_servers_with_the_identity_on_both_acknowledges()
    {
        var merged = await MergeAsync([Advert("n1", 1, NodeAdvert.Km, Identity("g"))], [Advert("n1", 3, NodeAdvert.Km, Identity("g"))]);

        merged.Should().ContainSingle().Which.Acknowledges("g", State()).Should().BeTrue();
        merged[0].Age.Should().Be(TimeSpan.FromSeconds(3), "the Dst advert, aged on the Dst clock");
    }

    [Fact]
    public async Task A_node_fresh_on_one_server_only_is_present_and_does_not_acknowledge()
    {
        var merged = await MergeAsync([Advert("plain", 1, NodeAdvert.Km, Identity("g"))], []);

        merged.Should().ContainSingle();
        merged[0].Age.Should().BeLessThanOrEqualTo(Fresh);
        merged[0].Acknowledges("g", State()).Should().BeFalse();
        merged[0].IdentityOf("g").Should().BeNull();
    }

    [Fact]
    public async Task A_stale_advert_on_the_other_server_means_the_node_is_fresh_on_one_only_and_does_not_acknowledge()
    {
        var merged = await MergeAsync([Advert("n1", 2, Identity("g"))], [Advert("n1", 600, Identity("g"))]);

        merged.Should().ContainSingle().Which.Acknowledges("g", State()).Should().BeFalse();
    }

    [Fact]
    public async Task Two_fresh_adverts_that_differ_never_acknowledge_whichever_has_the_lower_age()
    {
        var old = NodeAdvert.Identity("g", new AckedIdentity(4, "old", "S1", "fwd"));

        // The Dst advert is the younger by its own clock and carries the identity; the Src one is also fresh and carries the old one. No clock decides between them.
        var merged = await MergeAsync([Advert("n1", 5, old)], [Advert("n1", 0.5, Identity("g"))]);

        merged.Should().ContainSingle().Which.Acknowledges("g", State()).Should().BeFalse();
    }

    [Fact]
    public async Task A_bypass_or_a_suspicion_on_any_fresh_advert_counts_and_the_key_move_flag_needs_every_one()
    {
        var merged = await MergeAsync([Advert("n1", 1, NodeAdvert.Km, NodeAdvert.Bypass)], [Advert("n1", 1, NodeAdvert.CtlQuestion)]);

        merged[0].Bypasses.Should().BeTrue();
        merged[0].Suspects.Should().BeTrue();
        merged[0].HasKm.Should().BeFalse();
    }

    [Fact]
    public async Task Nodes_merge_by_id_and_a_node_on_the_dst_only_is_kept()
    {
        var merged = await MergeAsync([Advert("a", 1, Identity("g"))], [Advert("a", 1, Identity("g")), Advert("b", 2, Identity("g"))]);

        merged.Select(m => m.Node).Should().BeEquivalentTo("a", "b");
        merged.Single(m => m.Node == "a").Acknowledges("g", State()).Should().BeTrue();
        merged.Single(m => m.Node == "b").Acknowledges("g", State()).Should().BeFalse();
    }

    [Fact]
    public async Task A_node_stale_on_both_servers_stays_stale()
    {
        var merged = await MergeAsync([Advert("n1", 100)], [Advert("n1", 200)]);

        merged.Single().Age.Should().BeGreaterThan(Fresh);
    }

    [Fact]
    public async Task A_server_that_cannot_be_read_is_never_an_empty_node_list()
    {
        var merge = new TwoServerAdverts(new FixedAdverts([]), new FailingAdverts(), Fresh);

        var act = () => merge.ReadAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    private static NodeAdvert Advert(string node, double ageSeconds, params string[] tokens) => new(node, TimeSpan.FromSeconds(ageSeconds), tokens);

    private static string Identity(string group) => NodeAdvert.Identity(group, Acked);

    private static StateRecord State() => new("S1", "fwd", 5, "idx", true, 0, string.Empty, null, "a", "cs", string.Empty, string.Empty, string.Empty, string.Empty);

    private static async Task<IReadOnlyList<NodeAdvert>> MergeAsync(NodeAdvert[] src, NodeAdvert[] dst) =>
        await new TwoServerAdverts(new FixedAdverts(src), new FixedAdverts(dst), Fresh).ReadAsync(TestContext.Current.CancellationToken);

    private sealed class FixedAdverts(NodeAdvert[] adverts) : IAdvertSource
    {
        public Task<IReadOnlyList<NodeAdvert>> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<NodeAdvert>>(adverts);
    }

    private sealed class FailingAdverts : IAdvertSource
    {
        public Task<IReadOnlyList<NodeAdvert>> ReadAsync(CancellationToken cancellationToken) => Task.FromException<IReadOnlyList<NodeAdvert>>(new TimeoutException("the Dst is down"));
    }
}
