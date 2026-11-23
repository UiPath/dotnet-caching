using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

// Unit granularity and -1 re-admission of the admission counter.
public class EpochGateTests
{
    private const string Group = "g1";

    [Fact]
    public void A_node_with_no_identity_admits_nothing()
    {
        new EpochGate().Enter(Group).Should().BeNull();
    }

    [Fact]
    public void A_unit_counts_under_the_identity_it_was_admitted_under_until_it_completes()
    {
        var gate = new EpochGate();
        gate.Switch(Group, 5, "a");
        var unit = gate.Enter(Group)!;
        gate.Switch(Group, 6, "b");

        gate.OlderUnits(Group).Should().Be(1);
        unit.Should().Match<AdmittedUnit>(u => u.Ep == 5 && u.Id == "a");

        unit.Dispose();
        gate.OlderUnits(Group).Should().Be(0);
    }

    [Fact]
    public void A_unit_admitted_after_the_switch_is_counted_in_the_new_identity_and_blocks_no_ack()
    {
        var gate = new EpochGate();
        gate.Switch(Group, 5, "a");
        gate.Switch(Group, 6, "b");

        using var unit = gate.Enter(Group)!;

        gate.OlderUnits(Group).Should().Be(0);
        unit.Ep.Should().Be(6);
    }

    [Fact]
    public void A_readmitted_unit_moves_to_the_identity_in_force_so_the_drain_waits_for_re_runs_not_for_a_clock()
    {
        var gate = new EpochGate();
        gate.Switch(Group, 5, "a");
        var unit = gate.Enter(Group)!;
        unit.Readmit().Should().BeFalse("the identity did not change, so there is nothing to re-run");

        gate.Switch(Group, 6, "b");
        gate.OlderUnits(Group).Should().Be(1);
        unit.Readmit().Should().BeTrue();

        gate.OlderUnits(Group).Should().Be(0);
        unit.Should().Match<AdmittedUnit>(u => u.Ep == 6 && u.Id == "b");
        unit.Dispose();
        gate.OlderUnits(Group).Should().Be(0);
    }

    [Fact]
    public void A_lower_identity_is_ignored_and_disposing_twice_counts_once()
    {
        var gate = new EpochGate();
        gate.Switch(Group, 6, "b");
        gate.Switch(Group, 5, "a");
        var unit = gate.Enter(Group)!;

        unit.Ep.Should().Be(6);
        unit.Dispose();
        unit.Dispose();
        gate.OlderUnits(Group).Should().Be(0);
        var again = () => unit.Readmit();
        again.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Promotion_units_are_counted_apart_for_the_pi_token()
    {
        var gate = new EpochGate();
        gate.Switch(Group, 5, "a");
        var write = gate.Enter(Group)!;
        var promotion = gate.Enter(Group, promotion: true)!;

        gate.PromotionUnits(Group).Should().Be(1);
        gate.Switch(Group, 6, "b");
        promotion.Readmit().Should().BeTrue();
        gate.PromotionUnits(Group).Should().Be(1);
        promotion.Dispose();
        write.Dispose();

        gate.PromotionUnits(Group).Should().Be(0);
        gate.OlderUnits(Group).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_units_and_switches_leave_no_count_behind()
    {
        var gate = new EpochGate();
        gate.Switch(Group, 1, "a");
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var n = 0; n < 2000; n++)
            {
                using var unit = gate.Enter(Group)!;
                unit.Readmit();
            }
        })).ToArray();
        for (var ep = 2; ep < 50; ep++)
        {
            gate.Switch(Group, ep, "id" + ep);
            await Task.Yield();
        }

        await Task.WhenAll(workers);

        gate.OlderUnits(Group).Should().Be(0);
        gate.PromotionUnits(Group).Should().Be(0);
    }
}
