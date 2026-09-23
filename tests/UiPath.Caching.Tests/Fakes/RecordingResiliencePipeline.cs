using UiPath.Caching.Policies;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>Runs the callback once, recording its result type.</summary>
internal sealed class RecordingResiliencePipeline : IResiliencePipeline
{
    private readonly List<Type> _resultTypes = [];

    public IReadOnlyList<Type> ResultTypes
    {
        get
        {
            lock (_resultTypes)
            {
                return [.. _resultTypes];
            }
        }
    }

    public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
    {
        lock (_resultTypes)
        {
            _resultTypes.Add(typeof(TResult));
        }

        return callback(cancellationToken);
    }
}
