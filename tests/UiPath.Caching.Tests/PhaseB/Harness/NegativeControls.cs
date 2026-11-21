namespace UiPath.Caching.Tests.PhaseB.Harness;

using static Scenarios;

// T-B2: each control weakens the model in one way, and the harness must report the invariant the weakness exists to protect.
internal static class NegativeControls
{
    public static IReadOnlyList<NegativeControl> All { get; } =
    [
        new("length-only token", new() { LengthOnlyToken = true }, f => Forward(f, Origin(), Reader(), Deleter("D2", 1)), ViolationKinds.Stale),
        new("post-publish recheck instead of a fence", new() { PostPublishRecheck = true }, f => Forward(f, Origin(), Reader(), Deleter("D2", 1), Reader("P2", 1)), ViolationKinds.Stale),
        new("absent token passes", new() { AbsentTokenPasses = true }, f => Forward(f, Origin(), Reader(), Deleter("D2", 1), Evict(0, "G")), ViolationKinds.Stale),
        new("counter tokens a lagging master can reach", new() { CounterTokens = true }, f => Forward(f, Origin(), Reader(), Write("D2", 1, 0, string.Empty, WriterOp.Delete, WriterOp.Delete), Evict(0, "G")), ViolationKinds.Stale),
        new("server revocation removed (R1 scripts not refused after EPSYNC); admission draining itself is not modelled", new() { SourceScriptsSkipIdentity = true }, f => Build(f, S1, Both(S1), [Origin()], Reader("P", 1, false, "S3fwd"), Writer("W1", 0, "v1"), Transition(S1, S3, [1, 0])), ViolationKinds.Stale),
        new("NX in reverse", new() { ReverseNx = true }, f => Reverse(f, Origin() with { InitialL = "v0", InitialN = "v1" }, Reader(), Reader("M", 1, true), Reader("P2", 1)), ViolationKinds.Stale),
        new("Src read before CAP in a batch", new() { SourceReadBeforeCapture = true }, f => Forward(f, Origin(), Reader(), Deleter("D2", 1)), ViolationKinds.Stale),
        new("a Dst-first reverse write (H27)", new() { DstFirstReverseWrite = true }, f => Reverse(f, NoOrigin() with { InitialL = null, InitialN = "v0" }, Reader(), Writer("W2", 1, "v1")), ViolationKinds.Stale),
        new("a control record that CTLSYNC can lower", new() { CtlSyncLowers = true }, f => Build(f, S1, Both(S1), [Origin()], Reader("P", 1, false, "S3fwd"), Writer("W1", 0, "v1"), Transition(S1, S3, [1, 0], staleReplay: true)), ViolationKinds.Stale),
        new("a control record that is deleted", new() { CtlDeletedAtTransition = true }, f => Build(f, S1, Both(S1), [Origin()], Reader("P", 1, false, "S3fwd"), Writer("W1", 0, "v1"), Transition(S1, S3, [1, 0], staleReplay: true)), ViolationKinds.Stale),
        new("a gate that an absent record treats as open", new() { AbsentRecordIsOpen = true }, f => Build(f, S1, Both(S1), [Origin()], Reader("P", 1, false, "S3fwd"), Writer("W1", 0, "v1"), Transition(S1, S3, [1, 0], evictControl: true)), ViolationKinds.Stale),
        new("a cache admitted into the set in S3", new() { CacheAdmittedInS3 = true }, f => Forward(f, Origin() with { Unmanaged = true }, new ReadActor("P", 0, 0, AdmittedView: true), new ReadActor("P2", 1, 0, AdmittedView: true), Writer("W2", 1, "v1")), ViolationKinds.Stale),
        new("the revision 7 holder rule for hash field writes in S1(rev)", new() { HolderRule = true }, f => Build(f, S1Rev, Both(S1Rev), [Origin() with { InitialL = "v0", Hash = true }], Reader("R", 1), Write("W", 0, 0, string.Empty, WriterOp.Set("v1")), new MergeActor("M", 1, 0, "f1", "vm")), ViolationKinds.PartialHash),
        new("a capture bound measured from the CAP reply", new() { CaptureBoundFromReply = true }, f => Build(new WorldConfig([Origin(ttl: 10)], f) { SplitCapReply = true }, S3, Both(S3), Reader(), Tick("3")), ViolationKinds.CaptureBound),
        new("an S5 write that stops deleting the Src copy before lswept", new() { S5SkipsSourceDelete = true }, f => Build(f, S5, [S5, S4], [Origin()], Reader("R", 1), Write("D5", 0, 0, string.Empty, WriterOp.Delete), Write("W5", 0, 0, string.Empty, WriterOp.Set("v1"))), ViolationKinds.Stale),
        new("lswept stored without a confirming pass", new() { LsweptWithoutConfirm = true }, f => Build(f, S5, [S5, S4], [Origin()], new SweepActor("S", 0, 0, 0), Fail("L:1"), Seq("DR", Write("D5", 0, 0, string.Empty, WriterOp.Delete), Reader("R", 1))), ViolationKinds.Stale),
        new("lswept stored without the WAIT on the L replicas", new() { LsweptWithoutWait = true }, f => Build(f, S5, [S5, S4], [Origin()], new SweepActor("S", 0, 0, 0), Fail("L:1"), Seq("DR", Write("D5", 0, 0, string.Empty, WriterOp.Delete), Reader("R", 1))), ViolationKinds.Stale),
        new("an S1 read that returns an L miss without confirming the identity", new() { S1MissWithoutIdentityCheck = true }, f => Build(f, S1, Both(S1), [NoOrigin()], Reader("R", 0), Reader("M", 1, true, "S3fwd"), Transition(S1, S3, [1, 0])), ViolationKinds.FalseMiss),
        new("a forward no-origin write that deletes the source first (detector check)", new() { ForwardNoOriginSourceFirst = true }, f => Forward(f, NoOrigin(), Reader(), Writer("W2", 1, "v1"), Reader("P2", 1)), ViolationKinds.FalseMiss),
    ];

    private static ReadActor Reader(string name = "P", int node = 0, bool mover = false, string needs = "") => new(name, node, 0, mover, needs);

    private static WriteActor Writer(string name, int node, string value) => Write(name, node, 0, string.Empty, WriterOp.Set(value));

    private static WriteActor Deleter(string name, int node) => Write(name, node, 0, string.Empty, WriterOp.Delete);

    private static SeqActor Seq(string name, Actor first, Actor second) => new(name, first.Node, first, [second]);

    private static World Forward(ModelFaults faults, KeySpec key, params Actor[] actors) => Build(faults, S3, Both(S3), [key], actors);

    private static World Reverse(ModelFaults faults, KeySpec key, params Actor[] actors) => Build(faults, S3Rev, Both(S3Rev), [key], actors);
}
