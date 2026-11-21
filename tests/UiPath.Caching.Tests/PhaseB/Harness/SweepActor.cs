namespace UiPath.Caching.Tests.PhaseB.Harness;

// The S5 sweep of L (3.8): delete the residue, confirm on the master, wait for replication, store lswept, and let one node see it.
internal sealed record SweepActor(string Name, int Node, int Key, int AdoptNode) : Actor(Name, Node)
{
    private const string Control = ShardStore.ControlKey;

    public int Pc { get; init; }

    public Identity? Unit { get; init; }

    public int Rounds { get; init; }

    public int Seen { get; init; }

    public override int Choices(World w) => Done ? 0 : 1;

    public override Actor Step(World w, int choice)
    {
        var spec = w.Config.Keys[Key];
        var faults = w.Config.Faults;
        var self = Unit is null ? this with { Unit = w.Nodes[Node] } : this;
        switch (self.Pc)
        {
            case 0:
                ModelScripts.SrcDel(w.L, spec.L, Control, self.Unit!.Id, faults);
                self.Note(w, "sweeps the residue");
                return self with { Pc = faults.LsweptWithoutConfirm ? 2 : 1 };
            case 1:
                // The confirming pass runs on the master and deletes what a failover brought back.
                if (w.L.Exists(spec.L) && self.Rounds < 3)
                {
                    ModelScripts.SrcDel(w.L, spec.L, Control, self.Unit!.Id, faults);
                    return self with { Rounds = self.Rounds + 1, Seen = w.LFailovers };
                }

                return self with { Pc = 2, Seen = w.LFailovers };
            case 2:
                // A failover seen since the pass restarts it: the WAIT is on a connection that no longer holds the deletes.
                if (!faults.LsweptWithoutConfirm && !faults.LsweptWithoutWait && w.LFailovers != self.Seen && self.Rounds < 3)
                {
                    return self with { Pc = 1, Rounds = self.Rounds + 1 };
                }

                if (!faults.LsweptWithoutWait)
                {
                    w.L.Past.Clear();
                    self.Note(w, "waits for the L replicas");
                }

                return self with { Pc = 3 };
            case 3:
                if (w.Auth.Id == self.Unit!.Id)
                {
                    w.Auth = w.Auth with { Lswept = true };
                }

                self.Note(w, "stores lswept");
                return self with { Pc = 4 };
            default:
                w.Nodes[AdoptNode] = w.Auth;
                return self with { Done = true };
        }
    }
}
