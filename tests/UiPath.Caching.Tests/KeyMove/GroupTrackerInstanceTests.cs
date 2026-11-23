using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>For an instance group a sample above the state is identity_conflict only while the Dst read is fresh; after a failed Dst read it is a newer identity pending.</summary>
public sealed class GroupTrackerInstanceTests
{
    private static readonly ControlSample Ahead = new(9, "newer", "S1", "fwd", true, NPop: 0, SrcHome: true, SrcDurable: true);

    private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_sample_above_a_fresh_dst_read_is_identity_conflict()
    {
        var tracker = new GroupTracker(new GroupRegistration("g", false, true, InstanceMove: true), _time);
        tracker.Probed(Ahead);
        tracker.Read(FakeStateReader.Record("S1", 5));

        tracker.View().Conflict.Should().Be("identity_conflict");
    }

    [Fact]
    public void A_sample_above_the_state_after_a_failed_dst_read_is_a_newer_identity_pending_and_a_fresh_read_ends_it()
    {
        var tracker = new GroupTracker(new GroupRegistration("g", false, true, InstanceMove: true), _time);
        tracker.Read(FakeStateReader.Record("S1", 5));
        tracker.ReadFailed();
        tracker.Probed(Ahead);

        var view = tracker.View();
        view.Conflict.Should().BeNull();
        view.DstReadFailed.Should().BeTrue();
        view.SampleKnown.Should().BeTrue();

        tracker.Read(FakeStateReader.Record("S1", 5));

        tracker.View().Conflict.Should().Be("identity_conflict");
    }

    [Fact]
    public void A_single_server_group_keeps_the_conflict_whatever_a_failed_read_did()
    {
        var tracker = new GroupTracker(new GroupRegistration("g", false, true), _time);
        tracker.Read(FakeStateReader.Record("S1", 5));
        tracker.ReadFailed();
        tracker.Probed(Ahead);

        tracker.View().Conflict.Should().Be("identity_conflict");
    }

    [Fact]
    public void The_dur_zero_clock_of_a_sample_runs_for_one_identity_and_restarts_for_another()
    {
        var tracker = new GroupTracker(new GroupRegistration("g", false, true, InstanceMove: true), _time);
        tracker.Probed(Ahead with { Durable = false, SrcDurable = false });
        _time.Advance(TimeSpan.FromSeconds(4));
        tracker.Probed(Ahead with { Durable = false, SrcDurable = false });

        tracker.View().SampleDurZeroFor.Should().Be(TimeSpan.FromSeconds(4));

        tracker.Probed(Ahead with { Id = "other", Durable = false, SrcDurable = false });

        tracker.View().SampleDurZeroFor.Should().Be(TimeSpan.Zero);

        tracker.Probed(Ahead);

        tracker.View().SampleDurZeroFor.Should().BeNull();
    }
}
