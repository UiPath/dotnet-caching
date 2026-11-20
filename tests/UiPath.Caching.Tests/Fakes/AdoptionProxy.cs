using System.Reflection;
using System.Runtime.ExceptionServices;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>
/// An inner cache, as a proxy, that also offers a snapshot read for adoption.
/// Over a target it forwards every call, and answers a read for adoption with the target's own read; otherwise <see cref="Answer"/> and <see cref="Adoption"/> answer.
/// </summary>
internal abstract class AdoptionProxyBase : DispatchProxy
{
    private readonly List<string> _calls = [];

    /// <summary>The cache that answers every call, when the proxy only adds the snapshot read to it.</summary>
    public object? Target { get; set; }

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

    public int Count(string name) => Calls.Count(call => call == name);

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

    protected void Record(string name)
    {
        lock (_calls)
        {
            _calls.Add(name);
        }
    }
}

/// <summary>An <c>ICache</c> proxy; it offers the snapshot read and nothing else.</summary>
internal class AdoptionProxy : AdoptionProxyBase, IMigrationCache
{
    /// <summary>With a target, whether a read for adoption goes through the target's batch read of one key rather than its single read.</summary>
    public bool BatchBacked { get; set; }

    public static ICache Over(ICache target, bool batchBacked = false)
    {
        var proxy = DispatchProxy.Create<ICache, AdoptionProxy>();
        var adoption = (AdoptionProxy)(object)proxy;
        adoption.Target = target;
        adoption.BatchBacked = batchBacked;
        return proxy;
    }

    async ValueTask<ICacheEntry<T?>> IMigrationCache.ReadSnapshotAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token)
        where T : default
    {
        Record(nameof(IMigrationCache.ReadSnapshotAsync) + ":" + cacheKey.Name);
        if (Target is not ICache target)
        {
            return (ICacheEntry<T?>)Adoption(cacheKey);
        }

        if (BatchBacked)
        {
            return (await target.GetCacheEntriesAsync<T>([cacheKey], policy, token).ConfigureAwait(false))[0].Value;
        }

        return await target.GetCacheEntryAsync<T>(cacheKey, policy, token).ConfigureAwait(false);
    }
}

/// <summary>An <c>IHashCache</c> proxy; it is a shared hash tier, and a versioned write over a target that is not one goes through the target's plain write, as an untracked write does.</summary>
internal class AdoptionHashProxy : AdoptionProxyBase, ISharedHashTier
{
    public static IHashCache Over(IHashCache target)
    {
        var proxy = DispatchProxy.Create<IHashCache, AdoptionHashProxy>();
        ((AdoptionHashProxy)(object)proxy).Target = target;
        return proxy;
    }

    void ISharedTier.Invalidated(CacheKey key) => (Target as ISharedTier)?.Invalidated(key);

    void ISharedTier.InvalidatedAll() => (Target as ISharedTier)?.InvalidatedAll();

    IDisposable ISharedTier.DemandMaster() => (Target as ISharedTier)?.DemandMaster() ?? NoMasterScope.Instance;

    async ValueTask<VersionedWrite> ISharedHashTier.SetVersionedAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, PeerFieldLimits limits, CancellationToken token)
        where T : default
    {
        if (Target is ISharedHashTier shared)
        {
            return await shared.SetVersionedAsync(cacheKey, values, options, policy, limits, token).ConfigureAwait(false);
        }

        var written = await ((IHashCache)Target!).SetAsync<T>(cacheKey, values, options, policy, token).ConfigureAwait(false);
        return new VersionedWrite(written, 0, null);
    }

    ValueTask<bool> ISharedHashTier.RemoveFieldsVersionedAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token)
        where T : default =>
        Target is ISharedHashTier shared
            ? shared.RemoveFieldsVersionedAsync<T>(cacheKey, fields, token)
            : ((IHashCache)Target!).RemoveFieldsAsync<T>(cacheKey, fields, token);

    bool ISharedHashTier.TryDecodeFields<T>(IReadOnlyDictionary<string, byte[]?> encoded, CachePolicy? policy, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out Dictionary<string, T?> values)
        where T : default
    {
        if (Target is ISharedHashTier target)
        {
            return target.TryDecodeFields<T>(encoded, policy, out values);
        }

        values = default!;
        return false;
    }

    ValueTask<ICacheEntry<IDictionary<string, T?>>> ISharedHashTier.ReadForAdoptionAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token)
        where T : default
    {
        Record(nameof(ISharedHashTier.ReadForAdoptionAsync) + ":" + cacheKey.Name);
        return Target is IHashCache target
            ? target.GetCacheEntryAsync<T>(cacheKey, policy, token)
            : new((ICacheEntry<IDictionary<string, T?>>)Adoption(cacheKey));
    }
}
