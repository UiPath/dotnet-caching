using System.Diagnostics.CodeAnalysis;

namespace UiPath.Platform.Caching.Policies;

[ExcludeFromCodeCoverage]
public sealed class EmptyResiliencePipeline : IResiliencePipeline
{
    public void Execute(Action callback) => callback();

    public void Execute(Action<CancellationToken> callback, CancellationToken cancellationToken = default) => callback(cancellationToken);

    public TResult Execute<TResult>(Func<CancellationToken, TResult> callback, CancellationToken cancellationToken = default) => callback(cancellationToken);

    public TResult Execute<TResult>(Func<TResult> callback) => callback();

    public ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> callback, CancellationToken cancellationToken = default) => callback(cancellationToken);

    public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, CancellationToken cancellationToken = default) => callback(cancellationToken);
}
