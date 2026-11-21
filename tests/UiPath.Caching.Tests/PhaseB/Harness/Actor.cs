namespace UiPath.Caching.Tests.PhaseB.Harness;

// One sequence of store commands. Each Step runs exactly one command (or one client decision) against the world, so the scheduler can interleave actors.
// An actor is an immutable record: a step returns the next state, and the record's text is part of the world's fingerprint.
internal abstract record Actor(string Name, int Node)
{
    public bool Done { get; init; }

    // How many alternatives the next step has: 0 when done or blocked, more than 1 for a step that explores several outcomes.
    public abstract int Choices(World w);

    public abstract Actor Step(World w, int choice);

    protected void Note(World w, string text) => w.Trace?.Add($"{Name} {text}");
}
