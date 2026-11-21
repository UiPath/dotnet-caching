using System.Collections.Immutable;

namespace UiPath.Caching.Tests.PhaseB.Harness;

// Several actors run one after another as one actor, for a read that starts after the same client's write.
internal sealed record SeqActor(string Name, int Node, Actor Current, ImmutableArray<Actor> Rest) : Actor(Name, Node)
{
    public int Index { get; init; }

    public override int Choices(World w) => Done ? 0 : Current.Choices(w);

    public override Actor Step(World w, int choice)
    {
        var next = Current.Step(w, choice);
        if (!next.Done)
        {
            return this with { Current = next };
        }

        return Index < Rest.Length ? this with { Current = Rest[Index], Index = Index + 1 } : this with { Current = next, Done = true };
    }
}
