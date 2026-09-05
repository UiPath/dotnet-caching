namespace UiPath.Caching.Policies;

/// <remarks>
/// <see cref="ExecuteAsync"/> must not complete before <c>callback</c> has — a timeout has to be cooperative,
/// cancelling the token it hands the callback and then waiting for it. The Redis tier gives the connection
/// values as borrowed memory (<see cref="IMemorySerializerProxy"/>) that the caller reclaims as soon as the
/// pipeline returns; a pipeline that gave up on a callback still in flight would let the connection send a
/// buffer that has since been reused. The Polly pipeline this library ships awaits the callback throughout.
/// </remarks>
public interface IResiliencePipeline
{
    ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default);
}
