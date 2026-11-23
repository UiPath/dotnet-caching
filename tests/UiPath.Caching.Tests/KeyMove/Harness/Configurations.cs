namespace UiPath.Caching.Tests.KeyMove.Harness;

using static Scenarios;

// Each configuration runs one named world to exhaustion with the specified protocol.
internal static class Configurations
{
    public static IReadOnlyDictionary<string, Func<World>> All { get; } = new Dictionary<string, Func<World>>
    {
        ["forward S3, origin-backed: promotion read, guarded write, guarded delete"] = () => Forward(Origin(), Reader(), Writer("guarded-write", 1, "v1"), Deleter("guarded-delete", 1)),
        ["forward S3, no origin: promotion read, guarded write, guarded delete"] = () => Forward(NoOrigin(), Reader(), Writer("guarded-write", 1, "v1"), Deleter("guarded-delete", 1)),
        ["forward S3, origin-backed: promotion read, mover read, guarded write"] = () => Forward(Origin(), Reader(), Reader("mover-read", 1, true), Writer("guarded-write", 1, "v1")),
        ["forward S3, no origin: promotion read, mover read, guarded delete"] = () => Forward(NoOrigin(), Reader(), Reader("mover-read", 1, true), Deleter("guarded-delete", 1)),
        ["forward S3, no origin: promotion read, mover read, guarded write then guarded delete"] = () => Forward(NoOrigin(), Reader(), Reader("mover-read", 1, true), Write("guarded-write", 1, 0, string.Empty, WriterOp.Set("v1"), WriterOp.Delete)),
        ["forward S3, origin-backed: promotion read, clear, guarded write"] = () => Forward(Origin(), Reader(), Clear("c1"), Writer("guarded-write", 1, "v1")),
        ["forward S3, no origin: promotion read, clear, guarded delete"] = () => Forward(NoOrigin(), Reader(), Clear("c2"), Deleter("guarded-delete", 1)),
        ["forward S3, no origin: mover read, clear, guarded write"] = () => Forward(NoOrigin(), Reader("mover-read", 0, true), Clear("c2"), Writer("guarded-write", 1, "v1")),
        ["forward S3, no origin: promotion read, eviction, guarded write"] = () => Forward(NoOrigin(), Reader(), Evict(0, "N", "G", "L", "ctlN", "ctlL"), Writer("guarded-write", 1, "v1")),
        ["forward S3, origin-backed: promotion read, eviction, guarded delete"] = () => Forward(Origin(), Reader(), Evict(0, "N", "G", "L", "ctlN", "ctlL"), Deleter("guarded-delete", 1)),
        ["forward S3, no origin: promotion read, failover, guarded write"] = () => Forward(NoOrigin(), Reader(), Fail("N:1", "L:1", "N:2", "L:2"), Writer("guarded-write", 1, "v1")),
        ["forward S3, origin-backed: mover read, failover, guarded delete"] = () => Forward(Origin(), Reader("mover-read", 0, true), Fail("N:1", "L:1", "N:2"), Deleter("guarded-delete", 1)),
        ["forward S3, origin-backed with a life: promotion read, expiry, mover read"] = () => Forward(Origin(ttl: 10), Reader(), Tick("1", "10"), Reader("mover-read", 1, true)),
        ["forward S3, origin-backed with a life: promotion read, expiry, guarded write"] = () => Forward(Origin(ttl: 10), Reader(), Tick("1", "10"), Write("guarded-write", 1, 0, string.Empty, WriterOp.Set("v1", 10))),
        ["forward S3, origin-backed with a life and a split CAP reply: promotion read, expiry, mover read"] = () => Build(new WorldConfig([Origin(ttl: 10)], ModelFaults.None) { SplitCapReply = true }, S3, Both(S3), Reader(), Tick("1", "3", "10"), Reader("mover-read", 1, true)),
        ["forward S3, hash: promotion read, guarded write, guarded delete"] = () => Forward(Origin() with { Hash = true }, Reader(), Writer("guarded-write", 1, "v1"), Deleter("guarded-delete", 1)),
        ["forward S3, hash, no origin: promotion read, mover read, guarded write"] = () => Forward(NoOrigin() with { Hash = true }, Reader(), Reader("mover-read", 1, true), Writer("guarded-write", 1, "v1")),
        ["forward S3, two readers: promotion read, peer promotion read, guarded delete"] = () => Forward(NoOrigin(), Reader(), Reader("peer-promotion-read", 1), Deleter("guarded-delete", 1)),
        ["forward S3, unmanaged cache: promotion read, guarded write, guarded delete"] = () => Forward(Origin() with { Unmanaged = true }, new ReadActor("promotion-read", 0, 0, AdmittedView: true), Writer("guarded-write", 1, "v1"), Deleter("guarded-delete", 1)),
        ["forward S3, two keys: promotion read, read of a second key, guarded write"] = () => Build(null, S3, Both(S3), [Origin(), NoOrigin()], Reader(), new ReadActor("read-of-a-second-key", 1, 1), Write("guarded-write", 1, 1, string.Empty, WriterOp.Set("v1"))),
        ["forward S3, hash with a life: promotion read, expiry, mover read"] = () => Forward(Origin(ttl: 10) with { Hash = true }, Reader(), Tick("1", "10"), Reader("mover-read", 1, true)),
        ["S2 freeze to S3, two keys: write, delete, replica read, transition"] = () => Build(null, S2, Both(S2), [Origin(), NoOrigin()], Write("write", 0, 0, string.Empty, WriterOp.Set("v1")), Write("delete", 0, 1, string.Empty, WriterOp.Delete), new ReadActor("replica-read", 1, 1), Transition(S2, S3, [1, 0])),
        ["reverse S4, residue in N: replica read, guarded write, guarded delete"] = () => Build(null, S4Rev, Both(S4Rev), [NoOrigin() with { InitialL = null, InitialN = "v0" }], Reader("replica-read", 0), Writer("guarded-write", 1, "v1"), Deleter("guarded-delete", 1)),
        ["forward S4, residue: replica read, guarded write, guarded delete"] = () => Build(null, S4, Both(S4), [NoOrigin()], Reader("replica-read", 0), Writer("guarded-write", 1, "v1"), Deleter("guarded-delete", 1)),
        ["forward S4, residue and a newer N: replica read, guarded delete"] = () => Build(null, S4, Both(S4), [NoOrigin() with { InitialN = "v1" }], Reader("replica-read", 0), Deleter("guarded-delete", 1)),
        ["S1 forward, origin-backed: source write, source delete, replica read"] = () => Build(null, S1, Both(S1), [Origin()], Reader("replica-read", 1), Writer("source-write", 0, "v1"), Deleter("source-delete", 0)),
        ["S1 to S3, no origin: source delete, transition, replica read"] = () => Build(null, S1, Both(S1), [NoOrigin()], Reader("replica-read", 1), Deleter("source-delete", 0), Transition(S1, S3, [1, 0])),
        ["S1 to S3, origin-backed: source write, transition, mover read"] = () => Build(null, S1, Both(S1), [Origin()], Reader("mover-read", 1, true, "S3fwd"), Writer("source-write", 0, "v1"), Transition(S1, S3, [1, 0])),
        ["S1 to S3, origin-backed: source delete, transition, promotion read"] = () => Build(null, S1, Both(S1), [Origin()], Reader("promotion-read", 1, false, "S3fwd"), Deleter("source-delete", 0), Transition(S1, S3, [1, 0])),
        ["S1 to S3 with a stale EPSYNC: source write, transition, promotion read"] = () => Build(null, S1, Both(S1), [Origin()], Reader("promotion-read", 1, false, "S3fwd"), Writer("source-write", 0, "v1"), Transition(S1, S3, [1, 0], staleReplay: true)),
        ["S1 to S3 with the record evicted: source write, transition, promotion read"] = () => Build(null, S1, Both(S1), [Origin()], Reader("promotion-read", 1, false, "S3fwd"), Writer("source-write", 0, "v1"), Transition(S1, S3, [1, 0], evictControl: true)),
        ["S3 to S1 reverse: promotion read, transition, write"] = () => Build(null, S3, Both(S3), [Origin()], Reader("promotion-read", 0), Transition(S3, S1Rev, [1, 0]), Write("write", 1, 0, "S1rev", WriterOp.Set("v1"))),
        ["S3 to S4, origin-backed: promotion read, clear, transition"] = () => Build(null, S3, Both(S3), [Origin()], Reader("promotion-read", 0), Clear("c1"), Transition(S3, S4, [1, 0])),
        ["S3 to S1 reverse, no origin: mover read, guarded delete, transition"] = () => Build(null, S3, Both(S3), [NoOrigin()], Reader("mover-read", 0, true), Deleter("guarded-delete", 1), Transition(S3, S1Rev, [1, 0])),
        ["reverse S3, origin-backed: promotion read, guarded write, guarded delete"] = () => Reverse(Origin() with { InitialL = null, InitialN = "v0" }, Reader(), Writer("guarded-write", 1, "v1"), Deleter("guarded-delete", 1)),
        ["reverse S3, no origin: promotion read, guarded write, guarded delete"] = () => Reverse(NoOrigin() with { InitialL = null, InitialN = "v0" }, Reader(), Writer("guarded-write", 1, "v1"), Deleter("guarded-delete", 1)),
        ["reverse S3, no origin: promotion read, mover read, guarded write"] = () => Reverse(NoOrigin() with { InitialL = null, InitialN = "v0" }, Reader(), Reader("mover-read", 1, true), Writer("guarded-write", 1, "v1")),
        ["reverse S3, junk in L: promotion read, mover read, peer promotion read"] = () => Reverse(Origin() with { InitialL = "v0", InitialN = "v1" }, Reader(), Reader("mover-read", 1, true), Reader("peer-promotion-read", 1)),
        ["reverse S3, junk in L, no origin: promotion read, peer promotion read, failover"] = () => Reverse(NoOrigin() with { InitialL = "v0", InitialN = "v1" }, Reader(), Reader("peer-promotion-read", 1), Fail("L:1", "N:1")),
        ["reverse S3, origin-backed with a life: promotion read, expiry, mover read"] = () => Reverse(Origin(ttl: 10) with { InitialL = null, InitialN = "v0" }, Reader(), Tick("1", "10"), Reader("mover-read", 1, true)),
        ["reverse S3, hash: promotion read, guarded write, guarded delete"] = () => Reverse(Origin() with { InitialL = null, InitialN = "v0", Hash = true }, Reader(), Writer("guarded-write", 1, "v1"), Deleter("guarded-delete", 1)),
        ["reverse S3, no origin: promotion read, clear, guarded write"] = () => Reverse(NoOrigin() with { InitialL = null, InitialN = "v0" }, Reader(), Clear("c2"), Writer("guarded-write", 1, "v1")),
        ["reverse S3, no origin: promotion read, failover, guarded write"] = () => Reverse(NoOrigin() with { InitialL = null, InitialN = "v0" }, Reader(), Fail("L:1", "N:1"), Writer("guarded-write", 1, "v1")),
        ["reverse S3, origin-backed: promotion read, eviction, guarded delete"] = () => Reverse(Origin() with { InitialL = null, InitialN = "v0" }, Reader(), Evict(0, "N", "L", "GL", "ctlL", "ctlN"), Deleter("guarded-delete", 1)),
        ["S1 reverse to S3 reverse, origin-backed: write, transition, promotion read"] = () => Build(null, S1Rev, Both(S1Rev), [Origin() with { InitialL = null, InitialN = "v0" }], Reader("promotion-read", 1, false, "S3rev"), Write("write", 0, 0, string.Empty, WriterOp.Set("v1")), Transition(S1Rev, S3Rev, [1, 0])),
        ["S1 reverse hash: write, merge, replica read"] = () => Build(null, S1Rev, Both(S1Rev), [Origin() with { InitialL = "v0", Hash = true }], Reader("replica-read", 1), Write("write", 0, 0, string.Empty, WriterOp.Set("v1")), new MergeActor("merge", 1, 0, "f1", "vm")),
        ["S5 residue: closed-stage delete, closed-stage write, union reader"] = () => Build(null, S5, [S5, S4], [Origin()], Reader("replica-read", 1), Write("closed-stage-delete", 0, 0, string.Empty, WriterOp.Delete), Write("closed-stage-write", 0, 0, string.Empty, WriterOp.Set("v1"))),
        ["S5 sweep: sweeper, failover, delete then union read"] = () => Build(null, S5, [S5, S4], [Origin()], new SweepActor("sweep", 0, 0, 0), Fail("L:1"), Seq("delete-then-read", Write("closed-stage-delete", 0, 0, string.Empty, WriterOp.Delete), Reader("replica-read", 1))),
    };

    // Configurations in which a control record is evicted: scripts refuse until a Rebuild, which the harness does not model, so an actor may stay blocked.
    public static IReadOnlySet<string> MayBlock { get; } = new HashSet<string>
    {
        "forward S3, no origin: promotion read, eviction, guarded write",
        "forward S3, origin-backed: promotion read, eviction, guarded delete",
        "S1 to S3 with the record evicted: source write, transition, promotion read",
        "reverse S3, origin-backed: promotion read, eviction, guarded delete",
    };

    private static ReadActor Reader(string name = "promotion-read", int node = 0, bool mover = false, string needs = "") => new(name, node, 0, mover, needs);

    private static WriteActor Writer(string name, int node, string value, string needs = "") => Write(name, node, 0, needs, WriterOp.Set(value));

    private static WriteActor Deleter(string name, int node, string needs = "") => Write(name, node, 0, needs, WriterOp.Delete);

    private static ClearActor Clear(string cid) => new("clear", 1, cid);

    private static SeqActor Seq(string name, Actor first, Actor second) => new(name, first.Node, first, [second]);

    private static World Forward(KeySpec key, params Actor[] actors) => Build(null, S3, Both(S3), [key], actors);

    private static World Reverse(KeySpec key, params Actor[] actors) => Build(null, S3Rev, Both(S3Rev), [key], actors);
}
