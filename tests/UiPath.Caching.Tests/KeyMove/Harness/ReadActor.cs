namespace UiPath.Caching.Tests.KeyMove.Harness;

// A read of one key under the node's snapshot: forward or reverse read-through promotion, the union reads of S1 and S4 to S5, or (Mover) the promotion alone.
// Each plan uses a program counter to track progress through the read. The oracle checks the result when the read ends.
internal sealed record ReadActor(string Name, int Node, int Key, bool Mover = false, string Needs = "", bool AdmittedView = false) : Actor(Name, Node)
{
    public int Pc { get; init; }

    public string Plan { get; init; } = string.Empty;

    public Identity? Unit { get; init; }

    public int Rid { get; init; } = -1;

    public bool Rerunning { get; init; }

    public int Reruns { get; init; }

    public int Retries { get; init; }

    public int Seq { get; init; }

    public int Lag { get; init; }

    public string? Token { get; init; }

    public string? Gate { get; init; }

    public long Tcap { get; init; }

    // Failovers seen when the capture was taken: a client that sees a connection restore since then abandons the promotion.
    public int Restores { get; init; }

    public long SentAt { get; init; }

    public long RecvAt { get; init; }

    public string? Snap { get; init; }

    public bool SnapHash { get; init; }

    public long SnapPx { get; init; }

    public long SnapAt { get; init; }

    public string? SnapVersion { get; init; }

    public string? Held { get; init; }

    public string? Result { get; init; }

    public string? StagingKey { get; init; }

    public override int Choices(World w)
    {
        if (Done)
        {
            return 0;
        }

        if (Rerunning)
        {
            return w.Auth != Unit ? 1 : 0;
        }

        if (Unit is null)
        {
            return Admitted(w) ? 1 : 0;
        }

        return Pc == 0 && ReadsReplicaFirst(Plan) ? 3 : 1;
    }

    public override Actor Step(World w, int choice)
    {
        var spec = w.Config.Keys[Key];
        if (Rerunning)
        {
            w.Nodes[Node] = w.Auth;
            var again = Reruns + 1;
            if (again > 3)
            {
                if (Rid >= 0)
                {
                    w.Oracle.DropRead(Key, Rid);
                }

                return this with { Done = true };
            }

            var unit = w.Auth;
            return this with { Rerunning = false, Reruns = again, Unit = unit, Plan = PlanOf(w, unit), Pc = 0, Retries = 0, Token = null };
        }

        var self = this;
        if (Unit is null)
        {
            var unit = w.Nodes[Node];
            self = this with { Unit = unit, Plan = PlanOf(w, unit), Rid = Mover ? -1 : w.Oracle.BeginRead(Key), SentAt = w.Now };
            self.Note(w, $"starts as {self.Plan} under {unit.St}{unit.Dir}");
            return self;
        }

        var lag = ReadsReplicaFirst(self.Plan) && self.Pc == 0 ? choice : 0;
        return self.Plan switch
        {
            "Plain" => self.PlainStep(w, spec),
            "S1" => self.S1Step(w, spec),
            "UnionFwd" => self.UnionStep(w, spec, lag, false),
            "UnionRev" => self.UnionStep(w, spec, lag, true),
            "Fwd" => self.ForwardStep(w, spec, lag),
            _ => self.ReverseStep(w, spec),
        };
    }

    private bool ReadsReplicaFirst(string plan) => !Mover && plan is "Fwd" or "UnionFwd" or "UnionRev";

    private static long Remaining(Entry e, long now) => e.ExpireAt == -1 ? -1 : e.ExpireAt - now;

    private static string? V(Entry? e) => e?.Version();

    private bool Admitted(World w)
    {
        var view = w.Nodes[Node];
        return Needs.Length == 0 || Needs == view.St + view.Dir;
    }

    private string PlanOf(World w, Identity unit)
    {
        var spec = w.Config.Keys[Key];
        if (spec.Unmanaged && !(AdmittedView && w.Config.Faults.CacheAdmittedInS3))
        {
            return "Plain";
        }

        return (unit.St, unit.Reverse) switch
        {
            ("S1", false) => unit.NPop ? "UnionFwd" : "S1",
            ("S2", false) => "S1",
            ("S1", true) => "UnionRev",
            ("S3", false) => "Fwd",
            ("S3", true) => "Rev",
            (_, true) => "UnionRev",
            _ => "UnionFwd",
        };
    }

    private ReadActor Fin(World w, string? value, bool replicaHit = false)
    {
        Note(w, $"returns {value ?? "a miss"}");
        if (Rid >= 0 && w.Oracle.EndRead(Key, Rid, value, Unit!.Dir, false, replicaHit) is { } violation)
        {
            w.Fail(violation);
        }

        return this with { Done = true, Result = value };
    }

    private ReadActor Rerun(World w)
    {
        Note(w, "was refused and will re-read the state");
        return this with { Rerunning = true };
    }

    private ReadActor To(int pc) => this with { Pc = pc };

    private string Nonce() => Nonces.Of(Name, Seq);

    private ReadActor PlainStep(World w, KeySpec spec) => Fin(w, V(w.L.Get(spec.L)));

    private ReadActor S1Step(World w, KeySpec spec)
    {
        if (Pc == 0)
        {
            var hit = V(w.L.Get(spec.L));
            return hit is not null || !spec.NoOrigin || w.Config.Faults.S1MissWithoutIdentityCheck ? Fin(w, hit) : To(1);
        }

        return w.L.HGet(ShardStore.ControlKey, "id") != Unit!.Id ? Rerun(w) : Fin(w, null);
    }

    private ReadActor UnionStep(World w, KeySpec spec, int lag, bool reverse)
    {
        switch (Pc)
        {
            case 0:
                return V(w.N.Replica(spec.N, lag, w.Now)) is { } replica ? Fin(w, replica, lag > 0) : To(1);
            case 1:
                if (V(w.N.Get(spec.N)) is { } master)
                {
                    return Fin(w, master);
                }

                return Unit!.St == "S5" ? Fin(w, null) : To(2);
            case 2:
                return V(w.L.Get(spec.L)) is { } held ? Fin(w, held) : reverse ? Fin(w, null) : To(3);
            default:
                return Fin(w, V(w.N.Get(spec.N)));
        }
    }

    // Forward promotion. Pc 0 to 3 read, 4 captures, 5 snapshots Src, 6 publishes, 7 and 8 clean up Src.
    private ReadActor ForwardStep(World w, KeySpec spec, int lag)
    {
        var faults = w.Config.Faults;
        var route = Route.Of(w, spec, false);
        var swapped = faults.SourceReadBeforeCapture && !spec.Hash;
        switch (Pc)
        {
            case 0:
                Note(w, $"reads N on a replica lagging {lag}");
                return Mover ? To(4) with { SentAt = w.Now } : V(w.N.Replica(spec.N, lag, w.Now)) is { } replica ? Fin(w, replica, lag > 0) : To(1);
            case 1:
                Note(w, "reads N on the master");
                return V(w.N.Get(spec.N)) is { } master ? Fin(w, master) : To(2);
            case 2:
                Note(w, "probes L");
                return w.L.Exists(spec.L) ? (swapped ? To(5) : To(4) with { SentAt = w.Now }) : To(3);
            case 3:
                return Fin(w, V(w.N.Get(spec.N)));
            case 4:
            {
                var cap = ModelScripts.Cap(route.Dst, w.Now, route.Fence, route.DstKey, ShardStore.ControlKey, w.Config.FenceTtl, Nonce(), Unit!.Id, spec.Cid, faults);
                var next = this with { Seq = Seq + 1 };
                Note(w, $"captures: code {cap.Code}, token {cap.Token}");
                if (cap.Code == -1)
                {
                    return next.Rerun(w);
                }

                if (cap.Code != 0)
                {
                    return next.Fin(w, null);
                }

                next = next with { Token = cap.Token, Gate = cap.Gate, Tcap = cap.Tcap, RecvAt = w.Now, Restores = w.LFailovers + w.NFailovers };
                if (w.Config.SplitCapReply)
                {
                    return next.To(40);
                }

                return next.AfterCapture(cap.DestinationExists, swapped);
            }

            case 40:
                return (this with { RecvAt = w.Now }).AfterCapture(route.Dst.Exists(route.DstKey), swapped);
            case 41:
                return Fin(w, V(w.N.Get(spec.N)));
            case 5:
                return Snapshot(w, spec, route, swapped, 3, 6, reverse: false);
            case 51:
                return Stage(w, route, spec, 6);
            case 6:
                return Publish(w, spec, route, reverse: false);
            case 60:
            {
                var now = route.Src.Get(route.SrcKey)?.Encode();
                return now == Snap ? Fin(w, SnapVersion) : To(61);
            }

            case 61:
                route.Dst.Scripted(() => route.Dst.Remove(route.DstKey));
                return Fin(w, SnapVersion);
            case 62:
                return Fin(w, V(w.N.Get(spec.N)) ?? SnapVersion);
            case 7:
                return route.Dst.Exists(route.DstKey) ? To(8) : Fin(w, SnapVersion);
            default:
                CompareDelete(w, spec, route);
                return Fin(w, SnapVersion);
        }
    }

    // Reverse promotion. Pc 0 reads N, 1 captures GL, 2 snapshots N, 3 publishes into L, 4 confirms and deletes N, 5 and 6 are the miss path.
    private ReadActor ReverseStep(World w, KeySpec spec)
    {
        var faults = w.Config.Faults;
        var route = Route.Of(w, spec, true);
        switch (Pc)
        {
            case 0:
            {
                var hit = V(w.N.Get(spec.N));
                if (Mover)
                {
                    return To(1) with { SentAt = w.Now };
                }

                return hit is null ? To(5) : To(1) with { Held = hit, SentAt = w.Now };
            }

            case 1:
            {
                var cap = ModelScripts.Cap(route.Dst, w.Now, route.Fence, route.DstKey, ShardStore.ControlKey, w.Config.FenceTtl, Nonce(), Unit!.Id, spec.Cid, faults);
                var next = this with { Seq = Seq + 1 };
                Note(w, $"captures: code {cap.Code}, token {cap.Token}");
                if (cap.Code == -1)
                {
                    return next.Rerun(w);
                }

                if (cap.Code != 0)
                {
                    return next.Fin(w, Held);
                }

                next = next with { Token = cap.Token, Gate = cap.Gate, Tcap = cap.Tcap, RecvAt = w.Now, Restores = w.LFailovers + w.NFailovers };
                return w.Config.SplitCapReply ? next.To(10) : next.To(2);
            }

            case 10:
                return (this with { RecvAt = w.Now }).To(2);
            case 2:
                return Snapshot(w, spec, route, false, -1, 3, reverse: true);
            case 21:
                return Stage(w, route, spec, 3);
            case 3:
                return Publish(w, spec, route, reverse: true);
            case 4:
                return route.Dst.Exists(route.DstKey) ? To(41) : Fin(w, Held);
            case 41:
                CompareDelete(w, spec, route);
                return Fin(w, Held);
            case 5:
                return V(w.N.Get(spec.N)) is { } confirmed ? Fin(w, confirmed) : To(6);
            default:
                return Fin(w, V(w.L.Get(spec.L)));
        }
    }

    private ReadActor AfterCapture(bool destinationExists, bool swapped) =>
        destinationExists ? To(41) : swapped && Snap is not null ? To(6) : To(5);

    // ReadSnapshot of Src: absent goes to missPc (or ends a reverse read), present checks the capture bound and moves on.
    private ReadActor Snapshot(World w, KeySpec spec, Route route, bool swapped, int missPc, int publishPc, bool reverse)
    {
        var entry = route.Src.Get(route.SrcKey);
        Note(w, $"reads the source: {entry?.Version() ?? "absent"}");
        if (entry is null)
        {
            return reverse ? Fin(w, Held) : To(missPc);
        }

        var version = entry.Version();
        if (Token is not null && w.LFailovers + w.NFailovers != Restores)
        {
            Note(w, "abandons the promotion after a connection restore");
            return reverse ? Fin(w, Held) : Fin(w, version);
        }

        var next = this with { Snap = entry.Encode(), SnapHash = entry.IsHash, SnapPx = Remaining(entry, w.Now), SnapAt = w.Now, SnapVersion = version };
        if (swapped && Token is null)
        {
            return next.To(4);
        }

        var from = w.Config.Faults.CaptureBoundFromReply ? RecvAt : SentAt;
        if (w.Now - from > w.Config.MaxCapture)
        {
            Note(w, "abandons the capture as too slow");
            return reverse ? Fin(w, Held) : next.Fin(w, version);
        }

        return entry.IsHash ? next.To(reverse ? 21 : 51) : next.To(publishPc);
    }

    private ReadActor Stage(World w, Route route, KeySpec spec, int publishPc)
    {
        var staging = spec.Staging(Nonce());
        route.Dst.Scripted(() => route.Dst.Put(staging, Entry.Hash(ParseFields(Snap!), w.Now + w.Config.FenceTtl)));
        return (this with { Seq = Seq + 1, StagingKey = staging }).To(publishPc);
    }

    private ReadActor Publish(World w, KeySpec spec, Route route, bool reverse)
    {
        var faults = w.Config.Faults;
        if (w.LFailovers + w.NFailovers != Restores)
        {
            Note(w, "abandons the promotion after a connection restore");
            return Fin(w, reverse ? Held : SnapVersion);
        }

        var mode = reverse && !faults.ReverseNx ? "set" : "nx";
        var next = this with { Seq = Seq + 1 };
        var px = SnapPx;
        long code;
        if (SnapHash)
        {
            code = ModelScripts.PubHash(route.Dst, w.Now, route.Fence, route.DstKey, StagingKey!, ShardStore.ControlKey, Token!, px, mode, Gate!, Unit!.Id, spec.Cid, Nonce(), w.Config.FenceTtl, Tcap, faults);
        }
        else
        {
            code = ModelScripts.Pub(route.Dst, w.Now, route.Fence, route.DstKey, ShardStore.ControlKey, Token!, SnapVersion!, false, px, mode, Gate!, Unit!.Id, spec.Cid, Nonce(), w.Config.FenceTtl, Tcap, faults);
        }

        Note(w, $"publishes, result {code}");
        var result = reverse ? Held : SnapVersion;
        if (code == 1)
        {
            if (px != -1 && SnapAt - Tcap > w.Config.MaxCapture)
            {
                w.Fail(new Violation(ViolationKinds.CaptureBound, $"{spec.Name}: a publish lost {SnapAt - Tcap} of its source's life, more than {w.Config.MaxCapture}"));
            }

            return faults.PostPublishRecheck && !reverse ? next.To(60) : next.To(reverse ? 4 : 7);
        }

        if (code == 2)
        {
            return reverse ? (faults.ReverseNx ? next.To(4) : next.Fin(w, result)) : next.To(62);
        }

        return code switch
        {
            -1 => next.Rerun(w),
            0 when !reverse && Retries < 2 => (next with { Retries = Retries + 1, Token = null }).To(1),
            _ => next.Fin(w, result),
        };
    }

    private void CompareDelete(World w, KeySpec spec, Route route)
    {
        var code = Snap is { } snap && SnapHash
            ? ModelScripts.CmpDelHash(route.Src, route.SrcKey, ShardStore.ControlKey, Unit!.Id, [.. ParseFields(snap)], w.Config.Faults)
            : ModelScripts.CmpDel(route.Src, route.SrcKey, ShardStore.ControlKey, Unit!.Id, Snap!, w.Config.Faults);
        Note(w, $"compare-deletes the source, result {code}");
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseFields(string encoded) =>
        encoded.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => { var i = p.IndexOf('=', StringComparison.Ordinal); return new KeyValuePair<string, string>(p[..i], p[(i + 1)..]); });
}
