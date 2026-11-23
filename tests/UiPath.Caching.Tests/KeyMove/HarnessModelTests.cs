using UiPath.Caching.Tests.KeyMove.Harness;
using static UiPath.Caching.Tests.KeyMove.Harness.Scenarios;

namespace UiPath.Caching.Tests.KeyMove;

// Checks of the harness itself: what a failover voids, when a promoter abandons, and what a call that gave up leaves behind.
public class HarnessModelTests
{
    [Fact]
    public void A_failover_that_loses_only_a_fence_write_does_not_void_the_key()
    {
        var w = Build(null, S3, Both(S3), [NoOrigin()], Fail("N:1"));
        w.N.Scripted(() => w.N.Put("mg:G:b", Entry.Str("f1")));

        w.Step(0, 0);

        w.N.Exists("mg:G:b").Should().BeFalse("the fence write was lost");
        w.Oracle.EndRead(0, w.Oracle.BeginRead(0), null, "fwd", false)!.Kind.Should().Be(ViolationKinds.FalseMiss);
    }

    [Fact]
    public void A_failover_that_loses_a_value_voids_the_key()
    {
        var w = Build(null, S3, Both(S3), [NoOrigin()], Fail("N:1"));
        w.N.Scripted(() => w.N.Put("N:b", Entry.Str("v0")));

        w.Step(0, 0);

        w.Oracle.EndRead(0, w.Oracle.BeginRead(0), null, "fwd", false).Should().BeNull();
    }

    [Fact]
    public void A_promoter_abandons_after_a_connection_restore_between_its_capture_and_its_snapshot()
    {
        var w = Build(null, S3, Both(S3), [Origin()], new ReadActor("P", 0, 0), Fail("N:1"));
        for (var i = 0; i < 5; i++)
        {
            w.Step(0, 0);
        }

        w.NFailovers++;
        w.Step(0, 0);

        w.Actors[0].Done.Should().BeTrue();
        ((ReadActor)w.Actors[0]).Result.Should().Be("v0", "the value is served from the source");
        w.N.Exists("N:a").Should().BeFalse("nothing was published");
        w.L.Exists("L:a").Should().BeTrue("the source is untouched");
    }

    [Fact]
    public void A_delete_that_gave_up_after_a_step_ends_its_interval_and_excuses_only_until_a_later_write()
    {
        var w = Build(null, S3, Both(S3), [NoOrigin()], Write("D", 0, 0, string.Empty, WriterOp.Delete));
        w.Step(0, 0);
        w.Step(0, 0);
        ModelScripts.CtlSync(w.N, ShardStore.ControlKey, 9, "i9", "S1", "rev", "1", "S3", "fwd", "1", "2", "i2", [("c2", "g9")], ModelFaults.None);
        w.Step(0, 0);

        w.Auth = S1Rev;
        while (!w.Actors[0].Done && w.Enabled(0))
        {
            w.Step(0, 0);
        }

        w.Actors[0].Done.Should().BeTrue();
        w.Oracle.EndRead(0, w.Oracle.BeginRead(0), null, "fwd", false).Should().BeNull("the call that gave up may have removed the key");

        w.Oracle.EndMutation(0, w.Oracle.BeginMutation(0, "v2"));
        w.Oracle.EndRead(0, w.Oracle.BeginRead(0), null, "fwd", false)!.Kind.Should().Be(ViolationKinds.FalseMiss);
    }
}
