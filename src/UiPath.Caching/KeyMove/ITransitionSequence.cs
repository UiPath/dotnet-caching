namespace UiPath.Caching.KeyMove;

internal interface ITransitionSequence
{
    Task<TransitionResult> RunAsync(string group, StateExpectation expected, StateTarget target, SequenceOptions? sequence, CancellationToken cancellationToken);

    Task<TransitionResult> AbandonAsync(string group, bool advertAcknowledges, long updatedAt, SequenceOptions? sequence, CancellationToken cancellationToken);

    Task<TransitionResult> ResumeAsync(string group, SequenceOptions? sequence, CancellationToken cancellationToken);
}
