namespace UiPath.Caching.Tests.PhaseB.Harness;

// C, the Clear of one cache (3.6): close the gate on both servers, sweep L and N, verify the close, reopen with an expectation.
internal sealed record ClearActor(string Name, int Node, string Cid) : Actor(Name, Node)
{
    private const string Control = ShardStore.ControlKey;

    public int Pc { get; init; }

    public Identity? Unit { get; init; }

    public int Retries { get; init; }

    public int Seq { get; init; }

    public string Mutations { get; init; } = string.Empty;

    // True once the sweep ran: a call that gives up after that may have deleted something.
    public bool Swept { get; init; }

    public override int Choices(World w) => Done ? 0 : 1;

    public override Actor Step(World w, int choice)
    {
        var keys = w.Config.Keys.Select((k, i) => (k, i)).Where(p => p.k.Cid == Cid).ToList();
        var self = this;
        if (Unit is null)
        {
            self = this with { Unit = w.Nodes[Node], Mutations = string.Join(',', keys.Select(p => $"{p.i}:{w.Oracle.BeginMutation(p.i, null)}")) };
            self.Note(w, $"starts a clear of {Cid}");
            return self;
        }

        var closed = $"CLOSED:{Name}.{self.Seq}";
        var id = self.Unit!.Id;
        switch (self.Pc)
        {
            case 0:
                return ModelScripts.Gate(w.L, Control, id, Cid, closed, string.Empty) == 1 ? self.To(1) : self.Fail(w);
            case 1:
                return ModelScripts.Gate(w.N, Control, id, Cid, closed, string.Empty) == 1 ? self.To(2) : self.To(8);
            case 2:
                w.L.Scripted(() => keys.ForEach(p => w.L.Remove(p.k.L)));
                return (self with { Swept = true }).To(3);
            case 3:
                w.N.Scripted(() => keys.ForEach(p => w.N.Remove(p.k.N)));
                return self.To(4);
            case 4:
                if (w.L.HGet(Control, "c:" + Cid) == closed && w.N.HGet(Control, "c:" + Cid) == closed)
                {
                    return self.To(5);
                }

                return self.Retries < 1 ? self with { Retries = self.Retries + 1, Seq = self.Seq + 1, Pc = 0 } : self.To(8);
            case 5:
                ModelScripts.Gate(w.L, Control, id, Cid, $"OPEN:{Name}.o{self.Seq}", closed);
                return self.To(6);
            case 6:
                ModelScripts.Gate(w.N, Control, id, Cid, $"OPEN:{Name}.o{self.Seq}", closed);
                foreach (var m in self.Mutations.Split(','))
                {
                    var parts = m.Split(':');
                    w.Oracle.EndMutation(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                }

                return self with { Done = true };
            case 8:
                ModelScripts.Gate(w.L, Control, id, Cid, $"OPEN:{Name}.a{self.Seq}", closed);
                return self.To(9);
            default:
                ModelScripts.Gate(w.N, Control, id, Cid, $"OPEN:{Name}.a{self.Seq}", closed);
                return self.Fail(w);
        }
    }

    private ClearActor To(int pc) => this with { Pc = pc };

    private ClearActor Fail(World w) => Gave(w) with { Done = true };

    private ClearActor Gave(World w)
    {
        foreach (var m in Mutations.Split(','))
        {
            var parts = m.Split(':');
            var key = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            w.Oracle.AbortMutation(key, int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));

            // A call that failed after its sweep may have removed the value, so a miss stays excused until a later write completes.
            if (Swept)
            {
                w.Oracle.Disturb(key);
            }
        }

        return this;
    }
}
