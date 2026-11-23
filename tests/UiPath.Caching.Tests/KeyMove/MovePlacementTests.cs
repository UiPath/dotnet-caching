using System.Net;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>Where each record of an instance move lives, as names, databases and slots.</summary>
public sealed class MovePlacementTests
{
    private const string Library = "lib:";
    private const string App = "billing";

    private static readonly MasterCaps Master = new(new DnsEndPoint("m", 6379), new Version(7, 4), true, true, true, true, 1, "noeviction");

    [Fact]
    public void The_state_and_the_lease_are_on_the_dst_unprefixed_and_in_one_slot()
    {
        var kit = new Kit("{t}:", srcCluster: false, dstCluster: true);

        var (stateDb, stateKey) = kit.Placement.State("g1");
        var (leadDb, leadKey) = kit.Placement.Lead("g1");

        stateDb.Should().BeSameAs(kit.Dst);
        leadDb.Should().BeSameAs(kit.Dst);
        ((string?)stateKey).Should().Be("lib:mg:{g1}:state");
        ((string?)leadKey).Should().Be("lib:mg:{g1}:lead");
        HashSlot.Of(stateKey.ToString()).Should().Be(HashSlot.Of(leadKey.ToString()), "the script fence checks both atomically");
    }

    [Fact]
    public void The_claim_authority_is_on_the_src_prefixed_view_and_the_mirror_on_the_dst_under_the_same_name()
    {
        var kit = new Kit("p:", srcCluster: false, dstCluster: false);

        var (authorityDb, authorityKey) = kit.Placement.Claim(RecordSide.Src);
        var (mirrorDb, mirrorKey) = kit.Placement.Claim(RecordSide.Dst);

        authorityDb.Should().BeSameAs(kit.Src, "a plain prefixed pod reads the same key");
        mirrorDb.Should().BeSameAs(kit.Dst);
        ((string?)authorityKey).Should().Be("lib:mx:{billing}");
        ((string?)mirrorKey).Should().Be("lib:mx:{billing}");
        kit.Placement.Names(RecordSide.Src).ConnectorPrefix.Should().Be("p:");
        kit.Placement.Names(RecordSide.Dst).ConnectorPrefix.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false, 1, 1)]
    [InlineData(false, true, 1, 16384)]
    [InlineData(true, true, 1, 16384)]
    public void Control_records_are_one_on_the_src_under_a_tagged_prefix_and_one_per_slot_on_a_cluster_dst(bool srcCluster, bool dstCluster, int srcRecords, int dstRecords)
    {
        var kit = new Kit(srcCluster ? "{tag}:" : "p:", srcCluster, dstCluster);

        kit.Placement.Control(RecordSide.Src, "g1").Should().HaveCount(srcRecords);
        var dst = kit.Placement.Control(RecordSide.Dst, "g1");
        dst.Should().HaveCount(dstRecords);
        dst.Select(static r => r.Key.ToString()).Distinct().Should().HaveCount(dstRecords);
        foreach (var (slot, key) in dst.Where(static r => r.Slot % 997 == 0))
        {
            HashSlot.Of(key.ToString()).Should().Be(dstRecords == 1 ? HashSlot.Of(key.ToString()) : slot, "each record sits in the slot it is for");
        }
    }

    [Fact]
    public void A_dst_that_enforces_slots_takes_per_slot_records_whatever_the_server_type_says()
    {
        var kit = new Kit("p:", srcCluster: false, dstCluster: false, dstCaps: Caps(slotEnforced: true));

        kit.Placement.Control(RecordSide.Dst, "g1").Should().HaveCount(16384);
        kit.Placement.Control(RecordSide.Src, "g1").Should().HaveCount(1);
    }

    [Fact]
    public void Every_dst_record_name_hashes_to_its_own_slot_and_the_src_record_to_the_tagged_prefix_slot()
    {
        var kit = new Kit("{tag}:", srcCluster: true, dstCluster: true);

        foreach (var (slot, key) in kit.Placement.Control(RecordSide.Dst, "g1"))
        {
            HashSlot.Of(key.ToString()).Should().Be(slot);
        }

        var src = kit.Placement.Control(RecordSide.Src, "g1");
        src.Should().ContainSingle();
        kit.Placement.Names(RecordSide.Src).SlotSet().Should().Equal(HashSlot.Of("{tag}:"));
    }

    [Fact]
    public void The_forward_fence_sits_in_the_native_keys_slot_on_the_dst()
    {
        var kit = new Kit("{tag}:", srcCluster: true, dstCluster: true);

        var fence = kit.Placement.ForwardFence("data:{k1}:x");

        HashSlot.Of(fence.ToString()).Should().Be(HashSlot.Of("data:{k1}:x"));
        kit.Placement.Invoking(p => p.ForwardFence("plain-key")).Should().Throw<ArgumentException>("an untagged native key has no slot a fence could share");
    }

    [Fact]
    public void A_fence_named_with_the_src_prefix_would_leave_the_native_keys_slot_on_the_dst()
    {
        // Negative control: with the Src's tagged prefix the name passes the check, since the prefix decides both slots, but on the unprefixed Dst the fence is elsewhere.
        var swapped = new MoveKeyNames("{tag}:", Library, cluster: true);
        var kit = new Kit("{tag}:", srcCluster: true, dstCluster: true);

        var wrong = swapped.ForwardFence("plain-key");

        HashSlot.Of(wrong.ToString()).Should().NotBe(HashSlot.Of("plain-key"));
        kit.Placement.Invoking(p => p.ForwardFence("plain-key")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_reverse_fence_sits_in_the_legacy_keys_slot_on_the_src_with_the_prefix()
    {
        var kit = new Kit("{tag}:", srcCluster: true, dstCluster: true);

        var fence = kit.Placement.ReverseFence("legacy:key");

        HashSlot.Of("{tag}:", fence.ToString()).Should().Be(HashSlot.Of("{tag}:", "legacy:key"));
    }

    [Fact]
    public void Adverts_are_read_from_each_servers_own_view()
    {
        var kit = new Kit("p:", srcCluster: false, dstCluster: false);

        kit.Placement.Adverts(RecordSide.Src).Should().BeSameAs(kit.Src);
        kit.Placement.Adverts(RecordSide.Dst).Should().BeSameAs(kit.Dst);
    }

    [Fact]
    public void A_group_name_that_cannot_take_part_in_a_move_is_refused_for_every_record()
    {
        var kit = new Kit("p:", srcCluster: false, dstCluster: false);

        kit.Placement.Invoking(p => p.State("a{b")).Should().Throw<ArgumentException>();
        kit.Placement.Invoking(p => p.Control(RecordSide.Dst, "a=b")).Should().Throw<ArgumentException>();
    }

    private static SideCaps Caps(bool slotEnforced = false) => new([Master], ScanMode.PerShard, slotEnforced, [], []);

    private sealed class Kit
    {
        public Kit(string prefix, bool srcCluster, bool dstCluster, SideCaps? srcCaps = null, SideCaps? dstCaps = null)
        {
            Src = Database();
            Dst = Database();
            Placement = new MovePlacement(MoveViews.Create(Src, Dst, prefix), App, Library, srcCaps ?? Caps(), dstCaps ?? Caps(), dstCluster, srcCluster);
        }

        public IDatabase Src { get; }

        public IDatabase Dst { get; }

        public MovePlacement Placement { get; }

        private static IDatabase Database()
        {
            var mux = InterceptingProxy.Wrap<IConnectionMultiplexer>(null!, (method, _) =>
                method.Name.StartsWith("add_", StringComparison.Ordinal) || method.Name.StartsWith("remove_", StringComparison.Ordinal) ? null : throw new NotSupportedException(method.Name));
            return InterceptingProxy.Wrap<IDatabase>(null!, (method, _) => method.Name == "get_Multiplexer" ? mux : throw new NotSupportedException(method.Name));
        }
    }
}
