using System.Reflection;
using System.Runtime.ExceptionServices;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>
/// An inner cache, as a proxy of <c>ICache</c> or <c>IHashCache</c>, that also offers the snapshot read for adoption.
/// Over a target it forwards every call, and answers a read for adoption with the target's own read; otherwise <see cref="Answer"/> and <see cref="Adoption"/> answer.
/// </summary>
internal class AdoptionProxy : DispatchProxy, IAdoptionReadCache, IAdoptionReadHashCache
{
    private readonly List<string> _calls = [];

    /// <summary>The cache that answers every call, when the proxy only adds the snapshot read to it.</summary>
    public object? Target { get; set; }

    /// <summary>With a target, whether a read for adoption goes through the target's batch read of one key rather than its single read.</summary>
    public bool BatchBacked { get; set; }

    /// <summary>Answers a plain call by method name and arguments with the value inside its task.</summary>
    public Func<string, object?[], object?> Answer { get; set; } = static (name, _) => throw new NotSupportedException(name);

    /// <summary>Answers a read for adoption with the entry, or throws.</summary>
    public Func<CacheKey, object> Adoption { get; set; } = static _ => throw new NotSupportedException(nameof(Adoption));

    public string[] Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public static ICache Over(ICache target, bool batchBacked = false)
    {
        var proxy = DispatchProxy.Create<ICache, AdoptionProxy>();
        var adoption = (AdoptionProxy)(object)proxy;
        adoption.Target = target;
        adoption.BatchBacked = batchBacked;
        return proxy;
    }

    public static IHashCache Over(IHashCache target)
    {
        var proxy = DispatchProxy.Create<IHashCache, AdoptionProxy>();
        ((AdoptionProxy)(object)proxy).Target = target;
        return proxy;
    }

    public int Count(string name) => Calls.Count(call => call == name);

    async ValueTask<ICacheEntry<T?>> IAdoptionReadCache.ReadForAdoptionAsync<T>(CacheKey cacheKey, CancellationToken token)
        where T : default
    {
        Record(nameof(IAdoptionReadCache.ReadForAdoptionAsync) + ":" + cacheKey.Name);
        if (Target is not ICache target)
        {
            return (ICacheEntry<T?>)Adoption(cacheKey);
        }

        if (BatchBacked)
        {
            return (await target.GetCacheEntriesAsync<T>([cacheKey], null, token).ConfigureAwait(false))[0].Value;
        }

        return await target.GetCacheEntryAsync<T>(cacheKey, null, token).ConfigureAwait(false);
    }

    ValueTask<ICacheEntry<IDictionary<string, T?>>> IAdoptionReadHashCache.ReadForAdoptionAsync<T>(CacheKey cacheKey, CancellationToken token)
        where T : default
    {
        Record(nameof(IAdoptionReadHashCache.ReadForAdoptionAsync) + ":" + cacheKey.Name);
        return Target is IHashCache target
            ? target.GetCacheEntryAsync<T>(cacheKey, null, token)
            : new((ICacheEntry<IDictionary<string, T?>>)Adoption(cacheKey));
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (Target is not null)
        {
            Record(targetMethod!.Name);
            try
            {
                return targetMethod.Invoke(Target, args);
            }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Throw(ex.InnerException!);
                throw;
            }
        }

        switch (targetMethod!.Name)
        {
            case "get_Name":
                return "Redis";
            case "Dispose":
                return null;
        }

        Record(targetMethod.Name);
        var answer = Answer(targetMethod.Name, args ?? []);
        return targetMethod.ReturnType.IsGenericType && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>)
            ? Activator.CreateInstance(targetMethod.ReturnType, answer)
            : answer;
    }

    private void Record(string name)
    {
        lock (_calls)
        {
            _calls.Add(name);
        }
    }
}
