using UiPath.Caching.Tests.PhaseB.Harness;

namespace UiPath.Caching.Tests.PhaseB;

// T-B5, the unit parts, against the model of the scripts on the fake store. Time is the explicit tick passed to each call; no assertion reads a clock.
public class FenceAndControlModelTests
{
    private const string Fence = "fence";
    private const string Dst = "dst";
    private const string Ctl = "ctl";
    private const string Src = "src";
    private const long Ttl = 100;

    private static readonly ModelFaults F = ModelFaults.None;

    [Fact]
    public void A_publish_is_refused_when_the_fence_was_evicted()
    {
        var (s, gate) = Store();
        var cap = Cap(s, 0, "n1");
        s.Remove(Fence);

        Pub(s, 0, cap.Token!, gate, "n2").Should().Be(0);
        s.Exists(Dst).Should().BeFalse();
    }

    [Fact]
    public void A_publish_is_refused_when_the_fence_expired()
    {
        var (s, gate) = Store();
        var cap = Cap(s, 0, "n1");
        s.Expire(Ttl);

        s.Exists(Fence).Should().BeFalse();
        Pub(s, Ttl, cap.Token!, gate, "n2").Should().Be(0);
    }

    [Fact]
    public void A_recreated_fence_holds_a_new_nonce_that_the_old_token_does_not_match()
    {
        var (s, gate) = Store();
        var first = Cap(s, 0, "n1");
        s.Remove(Fence);
        var second = Cap(s, 0, "n2");

        second.Token.Should().Be("n2").And.NotBe(first.Token);
        Pub(s, 0, first.Token!, gate, "n3").Should().Be(0);
        Pub(s, 0, second.Token!, gate, "n3").Should().Be(1);
    }

    [Fact]
    public void Every_script_of_an_older_identity_is_refused_after_a_newer_EPSYNC()
    {
        var (s, gate) = Store();
        s.Put(Src, Entry.Str("old"));
        s.Put(Dst, Entry.Str("dst"));
        Sync(s, 2, "id2");

        Cap(s, 0, "n1", "id1").Code.Should().Be(-1);
        Pub(s, 0, "n1", gate, "n2", "id1").Should().Be(-1);
        ModelScripts.Bump(s, 0, Fence, Dst, Ctl, Ttl, "del", string.Empty, false, -1, "b", "id1", F).Should().Be(-1);
        ModelScripts.Fill(s, 0, Fence, Dst, Ctl, "v", false, -1, "f", Ttl, "id1", F).Should().Be(-1);
        ModelScripts.SrcDel(s, Src, Ctl, "id1", F).Should().Be(-1);
        ModelScripts.SrcSet(s, 0, Src, Ctl, "id1", "v", false, -1, "set", F).Should().Be(-1);
        ModelScripts.SrcExp(s, 0, Src, Ctl, "id1", 10, F).Should().Be(-1);
        ModelScripts.CmpDel(s, Src, Ctl, "id1", "old", F).Should().Be(-1);
        ModelScripts.Gate(s, Ctl, "id1", "c1", "CLOSED:x", string.Empty).Should().Be(-1);

        s.GetString(Src).Should().Be("old");
        s.GetString(Dst).Should().Be("dst");
        s.Exists(Fence).Should().BeFalse();
    }

    [Fact]
    public void A_delayed_publish_is_refused_after_a_gate_close_and_after_a_reopen()
    {
        var (s, gate) = Store();
        var cap = Cap(s, 0, "n1");
        s.Put("stg", Entry.Hash([new("a", "1")]));

        ModelScripts.Gate(s, Ctl, "id1", "c1", "CLOSED:x", string.Empty).Should().Be(1);
        Pub(s, 0, cap.Token!, gate, "n2").Should().Be(-2);
        ModelScripts.PubHash(s, 0, Fence, Dst, "stg", Ctl, cap.Token!, -1, "nx", gate, "id1", "c1", "n2", Ttl, -1, F).Should().Be(-2);
        s.Exists("stg").Should().BeFalse("a refused PUBH deletes its staging key");

        ModelScripts.Gate(s, Ctl, "id1", "c1", "OPEN:y", "CLOSED:x").Should().Be(1);
        Pub(s, 0, cap.Token!, gate, "n2").Should().Be(-2, "the reopened gate holds a new nonce");
        s.Exists(Dst).Should().BeFalse();
    }

    [Fact]
    public void A_stale_or_duplicate_CTLSYNC_does_not_change_a_newer_record()
    {
        var (s, _) = Store();
        Sync(s, 2, "id2").Should().Be(1);

        Sync(s, 1, "id1").Should().Be(0, "an older ep is ignored");
        Sync(s, 2, "id2").Should().Be(0, "a repeat is harmless");
        Sync(s, 2, "other").Should().Be(-5, "the same ep under another id conflicts");

        s.HGet(Ctl, "ep").Should().Be("2");
        s.HGet(Ctl, "id").Should().Be("id2");
    }

    [Fact]
    public void A_stale_CTLSYNC_after_a_release_does_not_reopen_the_old_identity()
    {
        var (s, gate) = Store();
        Sync(s, 2, "id2");
        ModelScripts.CtlSync(s, Ctl, 3, "id3", "S0", "fwd", "0", "S3", "rev", "1", "2", "id2", [("c1", "g3")], F).Should().Be(1);

        Sync(s, 2, "id2").Should().Be(0);
        Sync(s, 1, "id1").Should().Be(0);

        s.HGet(Ctl, "id").Should().Be("id3");
        s.HGet(Ctl, "st").Should().Be("S0");
        ModelScripts.SrcDel(s, Src, Ctl, "id2", F).Should().Be(-1);
        Pub(s, 0, "n1", gate, "n2", "id2").Should().Be(-1);
    }

    [Fact]
    public void A_missing_record_refuses_every_script()
    {
        var s = new ShardStore();
        s.Put(Src, Entry.Str("old"));

        Cap(s, 0, "n1").Code.Should().Be(-1);
        Pub(s, 0, "n1", "OPEN:g1", "n2").Should().Be(-1);
        ModelScripts.Bump(s, 0, Fence, Dst, Ctl, Ttl, "set", "v", false, -1, "b", "id1", F).Should().Be(-1);
        ModelScripts.Fill(s, 0, Fence, Dst, Ctl, "v", false, -1, "f", Ttl, "id1", F).Should().Be(-1);
        ModelScripts.SrcDel(s, Src, Ctl, "id1", F).Should().Be(-1);
        ModelScripts.SrcSet(s, 0, Src, Ctl, "id1", "v", false, -1, "nx", F).Should().Be(-1);
        ModelScripts.CmpDel(s, Src, Ctl, "id1", "old", F).Should().Be(-1);
        ModelScripts.Gate(s, Ctl, "id1", "c1", "CLOSED:x", string.Empty).Should().Be(-1);

        s.GetString(Src).Should().Be("old");
        s.Exists(Dst).Should().BeFalse();
        s.Exists(Fence).Should().BeFalse();
        s.Exists(Ctl).Should().BeFalse("a refused script never creates the record");
    }

    [Fact]
    public void A_failover_does_not_restore_an_entry_that_has_expired()
    {
        var (s, _) = Store();
        s.Scripted(() => s.Put(Fence, Entry.Str("old", 50)));
        s.Scripted(() => s.Put(Dst, Entry.Str("v")));

        s.Failover(1, 60).Should().Be(1);

        s.Exists(Fence).Should().BeFalse("the before-image held a fence that expired at 50");
        s.Exists(Dst).Should().BeFalse("the lost write is gone");
    }

    private static (ShardStore Store, string Gate) Store()
    {
        var s = new ShardStore();
        Sync(s, 1, "id1");
        ModelScripts.CtlCommit(s, Ctl, 1, "id1");
        return (s, "OPEN:g1");
    }

    private static long Sync(ShardStore s, long ep, string id) =>
        ModelScripts.CtlSync(s, Ctl, ep, id, "S3", "fwd", "1", "S1", "fwd", "0", "0", "id0", [("c1", ep == 1 ? "g1" : "g" + ep)], F);

    private static CapResult Cap(ShardStore s, long now, string nonce, string id = "id1") =>
        ModelScripts.Cap(s, now, Fence, Dst, Ctl, Ttl, nonce, id, "c1", F);

    private static long Pub(ShardStore s, long now, string token, string gate, string newNonce, string id = "id1") =>
        ModelScripts.Pub(s, now, Fence, Dst, Ctl, token, "v0", false, -1, "nx", gate, id, "c1", newNonce, Ttl, -1, F);
}
