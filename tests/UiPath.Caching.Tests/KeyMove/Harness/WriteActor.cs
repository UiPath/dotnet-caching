using System.Collections.Immutable;

namespace UiPath.Caching.Tests.KeyMove.Harness;

// Models writers and deleters for one key, including their S5 forms. Each actor runs its commands in the order required by the node's snapshot state.
// A script that answers -1 sends the unit back to re-read the state and start again under the new identity.
internal sealed record WriteActor(string Name, int Node, int Key, ImmutableArray<WriterOp> Ops, string Needs = "") : Actor(Name, Node)
{
    public int OpIndex { get; init; }

    public int Pc { get; init; }

    public string Plan { get; init; } = string.Empty;

    public Identity? Unit { get; init; }

    public int MutationId { get; init; } = -1;

    public bool Rerunning { get; init; }

    public int Reruns { get; init; }

    public int Seq { get; init; }

    // True once a step of this call committed: a call that gives up after that may have changed the key.
    public bool Touched { get; init; }

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

        if (Unit is null && OpIndex == 0 && Needs.Length > 0)
        {
            var view = w.Nodes[Node];
            return Needs == view.St + view.Dir ? 1 : 0;
        }

        return 1;
    }

    public override Actor Step(World w, int choice)
    {
        var spec = w.Config.Keys[Key];
        var op = Ops[OpIndex];
        if (Rerunning)
        {
            w.Nodes[Node] = w.Auth;
            var unit = w.Auth;
            var plan = PlanOf(w, spec, unit, op);
            if (Reruns + 1 > 3 || plan.Length == 0)
            {
                w.Oracle.AbortMutation(Key, MutationId);
                if (Touched)
                {
                    // A step committed before the call gave up, so the key may be gone; a miss stays excused until a later write completes.
                    w.Oracle.Disturb(Key);
                }

                return this with { Done = true };
            }

            return this with { Rerunning = false, Reruns = Reruns + 1, Unit = unit, Plan = plan, Pc = 0 };
        }

        var self = this;
        if (Unit is null)
        {
            var unit = w.Nodes[Node];
            var plan = PlanOf(w, spec, unit, op);
            if (plan.Length == 0)
            {
                // Closed for this cache in this state: the call fails unavailable and writes nothing.
                return Advance(this with { Unit = null });
            }

            self = this with { Unit = unit, Plan = plan, Pc = 0, MutationId = w.Oracle.BeginMutation(Key, op.Value) };
            self.Note(w, $"starts {(op.IsDelete ? "delete" : "write " + op.Value)} under {unit.St}{unit.Dir} as {plan}");
            return self;
        }

        var step = self.Plan.Split(',')[self.Pc];
        var code = self.Execute(w, spec, op, step);
        self.Note(w, $"{step} returned {code}");
        if (code == -1)
        {
            return self with { Rerunning = true, Seq = self.Seq + 1 };
        }

        if (self.Pc + 1 < self.Plan.Split(',').Length)
        {
            return self with { Pc = self.Pc + 1, Seq = self.Seq + 1, Touched = true };
        }

        w.Oracle.EndMutation(Key, self.MutationId);
        return Advance(self with { Seq = self.Seq + 1 });
    }

    private static string PlanOf(World w, KeySpec spec, Identity unit, WriterOp op)
    {
        var faults = w.Config.Faults;
        if (spec.Unmanaged)
        {
            return op.IsDelete ? "PlainDel" : "PlainSet";
        }

        var del = op.IsDelete;
        if (unit.St is "S1" or "S2")
        {
            // S2 and S1(rev) close every write of a no-origin cache.
            if (spec.NoOrigin && (unit.Reverse || unit.St == "S2"))
            {
                return string.Empty;
            }

            return unit.Reverse ? (del ? "SrcDel,BumpDel" : "SrcSet,BumpDel") : del ? "SrcDel" : "SrcSet";
        }

        if (unit.St == "S5")
        {
            var skip = unit.Lswept || faults.S5SkipsSourceDelete;
            return (skip ? string.Empty : "SrcDel,") + (del ? "DelN" : "SetN");
        }

        if (del)
        {
            return "SrcDel,BumpDel";
        }

        if (!unit.Reverse && spec.NoOrigin && !faults.ForwardNoOriginSourceFirst)
        {
            return "BumpSet,SrcDel";
        }

        return unit.Reverse && faults.DstFirstReverseWrite ? "BumpSet,SrcDel" : "SrcDel,BumpSet";
    }

    private static void PlainPut(World w, ShardStore shard, string key, KeySpec spec, WriterOp op)
    {
        shard.Scripted(() =>
        {
            shard.Put(key, Entry.OfVersion(op.Value!, spec.Hash));
            if (op.Px != -1)
            {
                shard.ExpireAt(key, w.Now + op.Px, w.Now);
            }
        });
        NoteDeadline(w, shard, key);
    }

    private WriteActor Advance(WriteActor done) =>
        OpIndex + 1 < Ops.Length ? done with { OpIndex = OpIndex + 1, Pc = 0, Unit = null, MutationId = -1, Plan = string.Empty, Reruns = 0, Touched = false } : done with { Done = true };

    private long Execute(World w, KeySpec spec, WriterOp op, string step)
    {
        var faults = w.Config.Faults;
        var route = Route.Of(w, spec, Unit!.Reverse);
        var nonce = Nonces.Of(Name, Seq);
        const string ctl = ShardStore.ControlKey;
        switch (step)
        {
            case "SrcDel":
                return ModelScripts.SrcDel(route.Src, route.SrcKey, ctl, Unit.Id, faults);
            case "SrcSet":
            {
                var code = ModelScripts.SrcSet(route.Src, w.Now, route.SrcKey, ctl, Unit.Id, op.Value!, spec.Hash, op.Px, "set", faults);
                NoteDeadline(w, route.Src, route.SrcKey);
                return code;
            }

            case "BumpDel":
                return ModelScripts.Bump(route.Dst, w.Now, route.Fence, route.DstKey, ctl, w.Config.FenceTtl, "del", string.Empty, false, -1, nonce, Unit.Id, faults);
            case "BumpSet":
            {
                var code = ModelScripts.Bump(route.Dst, w.Now, route.Fence, route.DstKey, ctl, w.Config.FenceTtl, "set", op.Value!, spec.Hash, op.Px, nonce, Unit.Id, faults);
                NoteDeadline(w, route.Dst, route.DstKey);
                return code;
            }

            case "PlainDel":
                return w.L.Scripted(() => w.L.Remove(spec.L) ? 1 : 0);
            case "PlainSet":
                PlainPut(w, w.L, spec.L, spec, op);
                return 1;
            case "DelN":
                return w.N.Scripted(() => w.N.Remove(spec.N) ? 1 : 0);
            default:
                PlainPut(w, w.N, spec.N, spec, op);
                return 1;
        }
    }

    private static void NoteDeadline(World w, ShardStore shard, string key)
    {
        if (shard.Get(key) is { } entry)
        {
            w.Oracle.NoteDeadline(entry.Version(), entry.ExpireAt);
        }
    }
}
