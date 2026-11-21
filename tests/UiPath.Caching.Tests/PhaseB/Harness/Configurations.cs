namespace UiPath.Caching.Tests.PhaseB.Harness;

using static Scenarios;

// The configurations of T-B1: one named world each, run to exhaustion with the specified protocol.
internal static class Configurations
{
    public static IReadOnlyDictionary<string, Func<World>> All { get; } = new Dictionary<string, Func<World>>
    {
        ["forward S3, origin-backed: P, W2, D2"] = () => Forward(Origin(), Reader(), Writer("W2", 1, "v1"), Deleter("D2", 1)),
        ["forward S3, no origin: P, W2, D2"] = () => Forward(NoOrigin(), Reader(), Writer("W2", 1, "v1"), Deleter("D2", 1)),
        ["forward S3, origin-backed: P, M, W2"] = () => Forward(Origin(), Reader(), Reader("M", 1, true), Writer("W2", 1, "v1")),
        ["forward S3, no origin: P, M, D2"] = () => Forward(NoOrigin(), Reader(), Reader("M", 1, true), Deleter("D2", 1)),
        ["forward S3, no origin: P, M, W2 then D2"] = () => Forward(NoOrigin(), Reader(), Reader("M", 1, true), Write("W2", 1, 0, string.Empty, WriterOp.Set("v1"), WriterOp.Delete)),
        ["forward S3, origin-backed: P, C, W2"] = () => Forward(Origin(), Reader(), Clear("c1"), Writer("W2", 1, "v1")),
        ["forward S3, no origin: P, C, D2"] = () => Forward(NoOrigin(), Reader(), Clear("c2"), Deleter("D2", 1)),
        ["forward S3, no origin: M, C, W2"] = () => Forward(NoOrigin(), Reader("M", 0, true), Clear("c2"), Writer("W2", 1, "v1")),
        ["forward S3, no origin: P, V, W2"] = () => Forward(NoOrigin(), Reader(), Evict(0, "N", "G", "L", "ctlN", "ctlL"), Writer("W2", 1, "v1")),
        ["forward S3, origin-backed: P, V, D2"] = () => Forward(Origin(), Reader(), Evict(0, "N", "G", "L", "ctlN", "ctlL"), Deleter("D2", 1)),
        ["forward S3, no origin: P, F, W2"] = () => Forward(NoOrigin(), Reader(), Fail("N:1", "L:1", "N:2", "L:2"), Writer("W2", 1, "v1")),
        ["forward S3, origin-backed: M, F, D2"] = () => Forward(Origin(), Reader("M", 0, true), Fail("N:1", "L:1", "N:2"), Deleter("D2", 1)),
        ["forward S3, origin-backed with a life: P, E, M"] = () => Forward(Origin(ttl: 10), Reader(), Tick("1", "10"), Reader("M", 1, true)),
        ["forward S3, origin-backed with a life: P, E, W2"] = () => Forward(Origin(ttl: 10), Reader(), Tick("1", "10"), Write("W2", 1, 0, string.Empty, WriterOp.Set("v1", 10))),
        ["forward S3, origin-backed with a life and a split CAP reply: P, E, M"] = () => Build(new WorldConfig([Origin(ttl: 10)], ModelFaults.None) { SplitCapReply = true }, S3, Both(S3), Reader(), Tick("1", "3", "10"), Reader("M", 1, true)),
        ["forward S3, hash: P, W2, D2"] = () => Forward(Origin() with { Hash = true }, Reader(), Writer("W2", 1, "v1"), Deleter("D2", 1)),
        ["forward S3, hash, no origin: P, M, W2"] = () => Forward(NoOrigin() with { Hash = true }, Reader(), Reader("M", 1, true), Writer("W2", 1, "v1")),
        ["forward S3, two readers: P, P2, D2"] = () => Forward(NoOrigin(), Reader(), Reader("P2", 1), Deleter("D2", 1)),
        ["forward S3, unmanaged cache: P, W2, D2"] = () => Forward(Origin() with { Unmanaged = true }, new ReadActor("P", 0, 0, AdmittedView: true), Writer("W2", 1, "v1"), Deleter("D2", 1)),
        ["forward S3, two keys: P, Q, W2"] = () => Build(null, S3, Both(S3), [Origin(), NoOrigin()], Reader(), new ReadActor("Q", 1, 1), Write("W2", 1, 1, string.Empty, WriterOp.Set("v1"))),
        ["forward S4, residue: R, W2, D2"] = () => Build(null, S4, Both(S4), [NoOrigin()], Reader("R", 0), Writer("W2", 1, "v1"), Deleter("D2", 1)),
        ["forward S4, residue and a newer N: R, D2"] = () => Build(null, S4, Both(S4), [NoOrigin() with { InitialN = "v1" }], Reader("R", 0), Deleter("D2", 1)),
        ["S1 forward, origin-backed: W1, D1, R"] = () => Build(null, S1, Both(S1), [Origin()], Reader("R", 1), Writer("W1", 0, "v1"), Deleter("D1", 0)),
        ["S1 to S3, no origin: D1, X, R"] = () => Build(null, S1, Both(S1), [NoOrigin()], Reader("R", 1), Deleter("D1", 0), Transition(S1, S3, [1, 0])),
        ["S1 to S3, origin-backed: W1, X, M"] = () => Build(null, S1, Both(S1), [Origin()], Reader("M", 1, true, "S3fwd"), Writer("W1", 0, "v1"), Transition(S1, S3, [1, 0])),
        ["S1 to S3, origin-backed: D1, X, P"] = () => Build(null, S1, Both(S1), [Origin()], Reader("P", 1, false, "S3fwd"), Deleter("D1", 0), Transition(S1, S3, [1, 0])),
        ["S1 to S3 with a stale EPSYNC: W1, X, P"] = () => Build(null, S1, Both(S1), [Origin()], Reader("P", 1, false, "S3fwd"), Writer("W1", 0, "v1"), Transition(S1, S3, [1, 0], staleReplay: true)),
        ["S1 to S3 with the record evicted: W1, X, P"] = () => Build(null, S1, Both(S1), [Origin()], Reader("P", 1, false, "S3fwd"), Writer("W1", 0, "v1"), Transition(S1, S3, [1, 0], evictControl: true)),
        ["S3 to S1 reverse: P, X, W"] = () => Build(null, S3, Both(S3), [Origin()], Reader("P", 0), Transition(S3, S1Rev, [1, 0]), Write("W", 1, 0, "S1rev", WriterOp.Set("v1"))),
        ["S3 to S4, origin-backed: P, C, X"] = () => Build(null, S3, Both(S3), [Origin()], Reader("P", 0), Clear("c1"), Transition(S3, S4, [1, 0])),
        ["S3 to S1 reverse, no origin: M, D2, X"] = () => Build(null, S3, Both(S3), [NoOrigin()], Reader("M", 0, true), Deleter("D2", 1), Transition(S3, S1Rev, [1, 0])),
        ["reverse S3, origin-backed: P, W2, D2"] = () => Reverse(Origin() with { InitialL = null, InitialN = "v0" }, Reader(), Writer("W2", 1, "v1"), Deleter("D2", 1)),
        ["reverse S3, no origin: P, W2, D2"] = () => Reverse(NoOrigin() with { InitialL = null, InitialN = "v0" }, Reader(), Writer("W2", 1, "v1"), Deleter("D2", 1)),
        ["reverse S3, no origin: P, M, W2"] = () => Reverse(NoOrigin() with { InitialL = null, InitialN = "v0" }, Reader(), Reader("M", 1, true), Writer("W2", 1, "v1")),
        ["reverse S3, junk in L: P, M, P2"] = () => Reverse(Origin() with { InitialL = "v0", InitialN = "v1" }, Reader(), Reader("M", 1, true), Reader("P2", 1)),
        ["reverse S3, junk in L, no origin: P, P2, F"] = () => Reverse(NoOrigin() with { InitialL = "v0", InitialN = "v1" }, Reader(), Reader("P2", 1), Fail("L:1", "N:1")),
        ["reverse S3, origin-backed with a life: P, E, M"] = () => Reverse(Origin(ttl: 10) with { InitialL = null, InitialN = "v0" }, Reader(), Tick("1", "10"), Reader("M", 1, true)),
        ["reverse S3, hash: P, W2, D2"] = () => Reverse(Origin() with { InitialL = null, InitialN = "v0", Hash = true }, Reader(), Writer("W2", 1, "v1"), Deleter("D2", 1)),
        ["reverse S3, no origin: P, C, W2"] = () => Reverse(NoOrigin() with { InitialL = null, InitialN = "v0" }, Reader(), Clear("c2"), Writer("W2", 1, "v1")),
        ["reverse S3, no origin: P, F, W2"] = () => Reverse(NoOrigin() with { InitialL = null, InitialN = "v0" }, Reader(), Fail("L:1", "N:1"), Writer("W2", 1, "v1")),
        ["reverse S3, origin-backed: P, V, D2"] = () => Reverse(Origin() with { InitialL = null, InitialN = "v0" }, Reader(), Evict(0, "N", "L", "GL", "ctlL", "ctlN"), Deleter("D2", 1)),
        ["S1 reverse to S3 reverse, origin-backed: W, X, P"] = () => Build(null, S1Rev, Both(S1Rev), [Origin() with { InitialL = null, InitialN = "v0" }], Reader("P", 1, false, "S3rev"), Write("W", 0, 0, string.Empty, WriterOp.Set("v1")), Transition(S1Rev, S3Rev, [1, 0])),
        ["S1 reverse hash: W, merge, R"] = () => Build(null, S1Rev, Both(S1Rev), [Origin() with { InitialL = "v0", Hash = true }], Reader("R", 1), Write("W", 0, 0, string.Empty, WriterOp.Set("v1")), new MergeActor("M", 1, 0, "f1", "vm")),
        ["S5 residue: D5, W5, union reader"] = () => Build(null, S5, [S5, S4], [Origin()], Reader("R", 1), Write("D5", 0, 0, string.Empty, WriterOp.Delete), Write("W5", 0, 0, string.Empty, WriterOp.Set("v1"))),
        ["S5 sweep: sweeper, F, delete then union read"] = () => Build(null, S5, [S5, S4], [Origin()], new SweepActor("S", 0, 0, 0), Fail("L:1"), Seq("DR", Write("D5", 0, 0, string.Empty, WriterOp.Delete), Reader("R", 1))),
    };

    private static ReadActor Reader(string name = "P", int node = 0, bool mover = false, string needs = "") => new(name, node, 0, mover, needs);

    private static WriteActor Writer(string name, int node, string value, string needs = "") => Write(name, node, 0, needs, WriterOp.Set(value));

    private static WriteActor Deleter(string name, int node, string needs = "") => Write(name, node, 0, needs, WriterOp.Delete);

    private static ClearActor Clear(string cid) => new("C", 1, cid);

    private static SeqActor Seq(string name, Actor first, Actor second) => new(name, first.Node, first, [second]);

    private static World Forward(KeySpec key, params Actor[] actors) => Build(null, S3, Both(S3), [key], actors);

    private static World Reverse(KeySpec key, params Actor[] actors) => Build(null, S3Rev, Both(S3Rev), [key], actors);
}
