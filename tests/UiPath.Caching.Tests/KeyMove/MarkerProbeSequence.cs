using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

// Exercises the driver’s marker guard after the claim hook, including on resume.
internal sealed class MarkerProbeSequence(ITransitionSequence inner) : ITransitionSequence
{
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    public List<string> Blocked { get; } = [];

    public Task<TransitionResult> RunAsync(string group, StateExpectation expected, StateTarget target, SequenceOptions? sequence, CancellationToken cancellationToken) =>
        inner.RunAsync(group, expected, target, Probe(sequence, "run"), cancellationToken);

    public Task<TransitionResult> AbandonAsync(string group, bool advertAcknowledges, long updatedAt, SequenceOptions? sequence, CancellationToken cancellationToken) =>
        inner.AbandonAsync(group, advertAcknowledges, updatedAt, sequence, cancellationToken);

    public Task<TransitionResult> ResumeAsync(string group, SequenceOptions? sequence, CancellationToken cancellationToken) =>
        inner.ResumeAsync(group, Probe(sequence, "resume"), cancellationToken);

    private SequenceOptions? Probe(SequenceOptions? options, string entry)
    {
        if (options?.AfterMarker is not { } guard)
        {
            return options;
        }

        return options with
        {
            BeforeCommit = async ct =>
            {
                if (options.BeforeCommit is { } before && await before(ct) is { } stopped)
                {
                    return stopped;
                }

                var result = await guard(new ConnectionProof(1, "run", "replication"), ct);
                if (result is not null)
                {
                    lock (_gate)
                    {
                        Blocked.Add(entry);
                    }
                }

                return result;
            },
        };
    }
}
