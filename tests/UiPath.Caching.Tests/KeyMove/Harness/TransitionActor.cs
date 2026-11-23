using System.Collections.Immutable;

namespace UiPath.Caching.Tests.KeyMove.Harness;

// X, one transition of the controller: EPSYNC and COMMIT on each server, DURABLE, then each node adopting the state; optionally a stale EPSYNC delivered late.
internal sealed record TransitionActor(string Name, Identity Target, Identity Previous, ImmutableArray<int> AdoptNodes, bool StaleReplay = false, bool EvictControl = false) : Actor(Name, -1)
{
    private const string Control = ShardStore.ControlKey;

    public int Pc { get; init; }

    public override int Choices(World w) => Done ? 0 : 1;

    public override Actor Step(World w, int choice)
    {
        var steps = Steps(w);
        var step = steps[Pc];
        switch (step)
        {
            case "EpL":
                Sync(w, w.L, Target);
                break;
            case "EpN":
                Sync(w, w.N, Target);
                break;
            case "CmL":
                ModelScripts.CtlCommit(w.L, Control, Target.Ep, Target.Id);
                break;
            case "CmN":
                ModelScripts.CtlCommit(w.N, Control, Target.Ep, Target.Id);
                break;
            case "Dur":
                w.Auth = Target;
                break;
            case "DelL":
                w.L.Scripted(() => w.L.Remove(Control));
                break;
            case "DelN":
                w.N.Scripted(() => w.N.Remove(Control));
                break;
            case "ReplayL":
                Sync(w, w.L, Previous);
                break;
            case "ReplayN":
                Sync(w, w.N, Previous);
                break;
            default:
                w.Nodes[int.Parse(step[5..], System.Globalization.CultureInfo.InvariantCulture)] = w.Auth;
                break;
        }

        Note(w, step);
        return Pc + 1 >= steps.Length ? this with { Done = true } : this with { Pc = Pc + 1 };
    }

    private string[] Steps(World w)
    {
        var steps = new List<string> { "EpL", "EpN", "CmL", "CmN", "Dur" };
        if (EvictControl)
        {
            steps.Add("DelL");
        }

        steps.AddRange(AdoptNodes.Select(n => "Adopt" + n));

        // The weakness: a retirement step that removes the records once the transition is adopted.
        if (w.Config.Faults.CtlDeletedAtTransition)
        {
            steps.AddRange(["DelL", "DelN"]);
        }

        if (StaleReplay)
        {
            steps.AddRange(["ReplayL", "ReplayN"]);
        }

        return [.. steps];
    }

    private void Sync(World w, ShardStore shard, Identity identity) =>
        ModelScripts.CtlSync(shard, Control, identity.Ep, identity.Id, identity.St, identity.Dir, identity.NPop ? "1" : "0", Previous.St, Previous.Dir, Previous.NPop ? "1" : "0", Previous.Ep.ToString(System.Globalization.CultureInfo.InvariantCulture), Previous.Id, [.. w.Config.Cids.Select(c => (c, "g" + identity.Ep))], w.Config.Faults);
}
