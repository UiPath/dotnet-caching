namespace UiPath.Caching.Tests.KeyMove.Harness;

// Two servers (the L side and the N side), a virtual clock, the controller's state, each node's snapshot of it, the actors and the oracle.
internal sealed class World
{
    private World(WorldConfig config)
    {
        Config = config;
        Auth = null!;
        Nodes = [];
        Actors = [];
        Oracle = null!;
        L = new ShardStore();
        N = new ShardStore();
    }

    public WorldConfig Config { get; }

    public long Now { get; set; }

    // Failovers of the L server, which a client sees as a connection restore.
    public int LFailovers { get; set; }

    public int NFailovers { get; set; }

    public ShardStore L { get; private set; }

    public ShardStore N { get; private set; }

    public Identity Auth { get; set; }

    public Identity[] Nodes { get; private set; }

    public Actor[] Actors { get; set; }

    public Oracle Oracle { get; private set; }

    public Violation? Violation { get; private set; }

    public List<string>? Trace { get; set; }

    public static World Create(WorldConfig config, Identity start, Identity[] nodeViews, params Actor[] actors)
    {
        var w = new World(config) { Auth = start, Nodes = [.. nodeViews], Actors = actors, Oracle = new Oracle(config.Keys) };
        foreach (var k in config.Keys)
        {
            if (k.InitialL is not null)
            {
                w.L.Put(k.L, Entry.OfVersion(k.InitialL, k.Hash, k.Ttl == -1 ? -1 : k.Ttl));
            }

            if (k.InitialN is not null)
            {
                w.N.Put(k.N, Entry.OfVersion(k.InitialN, k.Hash, k.Ttl == -1 ? -1 : k.Ttl));
            }
        }

        foreach (var shard in new[] { w.L, w.N })
        {
            ModelScripts.CtlSync(shard, ShardStore.ControlKey, start.Ep, start.Id, start.St, start.Dir, start.NPop ? "1" : "0", "S0", "none", "0", "0", "i0", [.. config.Cids.Select(c => (c, "g" + start.Ep))], ModelFaults.None);
            ModelScripts.CtlCommit(shard, ShardStore.ControlKey, start.Ep, start.Id);
        }

        // The start is not a write the harness may lose.
        w.L.Past.Clear();
        w.N.Past.Clear();
        return w;
    }

    public World Clone() => new(Config)
    {
        Now = Now,
        LFailovers = LFailovers,
        NFailovers = NFailovers,
        L = L.Clone(),
        N = N.Clone(),
        Auth = Auth,
        Nodes = [.. Nodes],
        Actors = [.. Actors],
        Oracle = Oracle.Clone(),
        Violation = Violation,
    };

    public void Fail(Violation violation) => Violation ??= violation;

    public bool Enabled(int actor) => !Actors[actor].Done && Actors[actor].Choices(this) > 0;

    // Runs one step of one actor and checks the state invariants.
    public void Step(int actor, int choice)
    {
        Actors[actor] = Actors[actor].Step(this, choice);
        if (Oracle.CheckEntries(this) is { } violation)
        {
            Fail(violation);
        }
    }

    public (ulong, ulong) Fingerprint()
    {
        var h = new StateHasher();
        h.Add(Now);
        h.Add(LFailovers);
        h.Add(NFailovers);
        L.Write(h);
        h.Add("|");
        N.Write(h);
        h.Add(Auth.ToString());
        foreach (var n in Nodes)
        {
            h.Add(n.ToString());
        }

        foreach (var a in Actors)
        {
            h.Add(a.ToString());
        }

        Oracle.Write(h);
        return h.Result();
    }
}
