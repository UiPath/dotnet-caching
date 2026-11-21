namespace UiPath.Caching.Tests.PhaseB.Harness;

// A field merge on a hash in S1(rev). The specified protocol fails it closed (3.7); the HolderRule fault is the revision 7 rule: find the side that holds the key, merge there, delete the other side.
internal sealed record MergeActor(string Name, int Node, int Key, string Field, string Value) : Actor(Name, Node)
{
    private const string Control = ShardStore.ControlKey;

    public int Pc { get; init; }

    public Identity? Unit { get; init; }

    public bool OnN { get; init; }

    public bool OnL { get; init; }

    public override int Choices(World w) => Done ? 0 : 1;

    public override Actor Step(World w, int choice)
    {
        var spec = w.Config.Keys[Key];
        var faults = w.Config.Faults;
        var self = Unit is null ? this with { Unit = w.Nodes[Node] } : this;
        if (!faults.HolderRule)
        {
            self.Note(w, "is refused: merge_blocked");
            return self with { Done = true };
        }

        switch (self.Pc)
        {
            case 0:
                return self with { Pc = 1, OnN = w.N.Exists(spec.N) };
            case 1:
                return self with { Pc = 2, OnL = w.L.Exists(spec.L) };
            case 2:
            {
                var onN = self.OnN;
                var shard = onN ? w.N : w.L;
                var key = onN ? spec.N : spec.L;
                ModelScripts.HSetC(shard, w.Now, key, Control, self.Unit!.Id, false, -1, null, null, 0, null, null, 0, [new(Field, Value)], faults);
                self.Note(w, $"merges {Field} on the {(onN ? "N" : "L")} side");
                return self with { Pc = 3 };
            }

            default:
            {
                var other = self.OnN ? w.L : w.N;
                var key = self.OnN ? spec.L : spec.N;
                ModelScripts.SrcDel(other, key, Control, self.Unit!.Id, faults);
                return self with { Done = true };
            }
        }
    }
}
