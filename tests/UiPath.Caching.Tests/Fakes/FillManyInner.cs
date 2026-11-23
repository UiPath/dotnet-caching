using System.Reflection;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>An inner cache that also fills several keys in one call; its <see cref="ICache"/> members go to the substitute behind it.</summary>
internal class FillManyInner : DispatchProxy, IFillManyTier
{
    private ICache _target = default!;

    /// <summary>The keys and expiration of each call.</summary>
    public List<(CacheKey[] Keys, DateTimeOffset Expiration)> Calls { get; } = [];

    /// <summary>What each key of a call answers; every key is added when none is set.</summary>
    public Func<CacheKey[], object?[], CancellationToken, FillOutcome[]>? Answer { get; set; }

    public static ICache Wrap(ICache target, out FillManyInner many)
    {
        var proxy = Create<ICache, FillManyInner>();
        many = (FillManyInner)(object)proxy;
        many._target = target;
        return proxy;
    }

    public ValueTask<FillResult<T>[]> TryFillManyAsync<T>(KeyValuePair<CacheKey, T?>[] entries, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token, TimeSpan? generatorElapsed = null)
    {
        var keys = Array.ConvertAll(entries, e => e.Key);
        lock (Calls)
        {
            Calls.Add((keys, expiration));
        }

        var outcomes = Answer?.Invoke(keys, Array.ConvertAll(entries, e => (object?)e.Value), token) ?? Array.ConvertAll(keys, _ => FillOutcome.Added);
        return new(Array.ConvertAll(outcomes, o => new FillResult<T>(o, default)));
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        try
        {
            return targetMethod!.Invoke(_target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
