using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The two-server control sample takes the highest ep and flags records that disagree at one ep.</summary>
public sealed class SampleMergeTests
{
    [Fact]
    public void The_highest_ep_of_either_server_wins()
    {
        SampleMerge.Highest(Record(5), Record(7, "b")).Should().Be(Record(7, "b"));
        SampleMerge.Highest(Record(9, "c") with { SrcHome = true, SrcDurable = true }, Record(7, "b")).Should().Match<ControlSample>(s => s.Ep == 9 && s.Id == "c" && s.SrcHome);
    }

    [Fact]
    public void A_server_with_no_record_leaves_the_other_one()
    {
        SampleMerge.Highest(null, Record(3)).Should().Be(Record(3));
        SampleMerge.Highest(Record(3), null).Should().Be(Record(3));
        SampleMerge.Highest(null, null).Should().BeNull();
    }

    [Theory]
    [InlineData("a", "a", "S1", "S1", false)]
    [InlineData("a", "b", "S1", "S1", true)]
    [InlineData("a", "a", "S1", "S2", true)]
    public void Records_at_one_ep_with_other_ids_or_states_conflict(string srcId, string dstId, string srcState, string dstState, bool conflicting) =>
        SampleMerge.Highest(Record(5, srcId, srcState), Record(5, dstId, dstState))!.Conflicting.Should().Be(conflicting);

    [Fact]
    public void At_one_ep_one_committed_record_commits_the_epoch_and_the_src_keeps_its_own_dur()
    {
        var merged = SampleMerge.Highest(Record(5, durable: false, np: 1), Record(5, durable: true, np: 1))!;

        merged.Durable.Should().BeTrue();
        merged.SrcHome.Should().BeTrue();
        merged.SrcDurable.Should().BeFalse("the fallback needs the Src record's own dur=1");
        merged.NPop.Should().Be(1);
    }

    [Fact]
    public void A_dst_record_above_the_src_has_no_src_home()
    {
        SampleMerge.Highest(Record(5), Record(6, "b"))!.SrcHome.Should().BeFalse();
    }

    private static ControlSample Record(long ep, string id = "a", string state = "S1", bool durable = true, int np = 0) =>
        new(ep, id, state, "fwd", durable, NPop: np);
}
