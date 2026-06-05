namespace UiPath.Platform.Caching.Policies;

[ExcludeFromCodeCoverage]
public sealed class EmptyResiliencePipeline : IResiliencePipeline
{
    public static readonly EmptyResiliencePipeline Instance = new();

    public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default) => callback(cancellationToken);
}
