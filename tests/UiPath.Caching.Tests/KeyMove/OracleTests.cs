using System.Collections.Immutable;
using UiPath.Caching.Tests.KeyMove.Harness;

namespace UiPath.Caching.Tests.KeyMove;

public class OracleTests
{
    private static readonly ImmutableArray<KeySpec> Keys = [new KeySpec("b", "c2", NoOrigin: true, InitialL: "v0")];

    [Fact]
    public void A_miss_after_an_eviction_is_excused()
    {
        var oracle = new Oracle(Keys);
        oracle.Disturb(0);

        oracle.EndRead(0, oracle.BeginRead(0), null, "fwd", false).Should().BeNull();
    }

    [Fact]
    public void A_wrong_loss_after_an_eviction_and_a_rewrite_is_caught()
    {
        var oracle = new Oracle(Keys);
        oracle.Lose(0);
        oracle.EndMutation(0, oracle.BeginMutation(0, "v1"));

        var violation = oracle.EndRead(0, oracle.BeginRead(0), null, "fwd", false);

        violation.Should().NotBeNull();
        violation!.Kind.Should().Be(ViolationKinds.FalseMiss);
    }

    [Fact]
    public void A_rewrite_that_started_before_the_eviction_does_not_restore_the_check()
    {
        var oracle = new Oracle(Keys);
        var write = oracle.BeginMutation(0, "v1");
        oracle.Disturb(0);
        oracle.EndMutation(0, write);

        oracle.EndRead(0, oracle.BeginRead(0), null, "fwd", false).Should().BeNull();
    }

    [Fact]
    public void A_clear_that_gave_up_stops_excusing_misses()
    {
        var oracle = new Oracle(Keys);
        var clear = oracle.BeginMutation(0, null);
        oracle.AbortMutation(0, clear);
        oracle.EndMutation(0, oracle.BeginMutation(0, "v1"));

        var violation = oracle.EndRead(0, oracle.BeginRead(0), null, "fwd", false);

        violation.Should().NotBeNull();
        violation!.Kind.Should().Be(ViolationKinds.FalseMiss);
    }

    [Fact]
    public void A_clear_still_running_excuses_a_miss()
    {
        var oracle = new Oracle(Keys);
        oracle.BeginMutation(0, null);

        oracle.EndRead(0, oracle.BeginRead(0), null, "fwd", false).Should().BeNull();
    }
}
