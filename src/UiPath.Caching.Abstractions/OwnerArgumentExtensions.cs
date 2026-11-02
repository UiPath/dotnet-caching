using System.Diagnostics.CodeAnalysis;

namespace UiPath.Caching;

/// <summary>Get-or-add overloads that hand a static generator the owner and one argument, without a tuple or a closure.</summary>
[SuppressMessage("Major Code Smell", "S2436:Types and methods should not have too many generic parameters", Justification = "The typed-cache overloads take the generator's task type as its own parameter, so only the receiver fixes T and a nullable T stays warning-free.")]
public static class OwnerArgumentExtensions
{
    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<T?> GetOrAddAsync<T, TOwner, TArg>(this ICache cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default) =>
        cache.GetOrAddAsync<T, OwnerArgument<TOwner, TArg, Task<T?>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, policy, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<T?> GetOrAddAsync<T, TOwner, TArg>(this ICache cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddAsync<T, OwnerArgument<TOwner, TArg, Task<T?>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, policy, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<T?> GetOrAddAsync<T, TOwner, TArg>(this ICache cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        cache.GetOrAddAsync<T, OwnerArgument<TOwner, TArg, Task<T?>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, policy, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<T?> GetOrAddAsync<T, TOwner, TArg>(this ICache cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddAsync<T, OwnerArgument<TOwner, TArg, Task<T?>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, policy, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<T?> GetOrAddAsync<T, TOwner, TArg>(this ICache cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        cache.GetOrAddAsync<T, OwnerArgument<TOwner, TArg, Task<T?>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, policy, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<T?> GetOrAddAsync<T, TOwner, TArg>(this ICache cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddAsync<T, OwnerArgument<TOwner, TArg, Task<T?>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, policy, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<T?> GetOrAddAsync<T, TOwner, TArg, TTask>(this ICache<T> cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, CancellationToken token = default)
        where TTask : Task<T?> =>
        cache.GetOrAddAsync<OwnerArgument<TOwner, TArg, Task<T?>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<T?> GetOrAddAsync<T, TOwner, TArg, TTask>(this ICache<T> cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, CancellationToken token = default)
        where TTask : Task<T?>
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddAsync<OwnerArgument<TOwner, TArg, Task<T?>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<T?> GetOrAddAsync<T, TOwner, TArg, TTask>(this ICache<T> cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, TimeSpan expiration, CancellationToken token = default)
        where TTask : Task<T?> =>
        cache.GetOrAddAsync<OwnerArgument<TOwner, TArg, Task<T?>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<T?> GetOrAddAsync<T, TOwner, TArg, TTask>(this ICache<T> cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, TimeSpan expiration, CancellationToken token = default)
        where TTask : Task<T?>
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddAsync<OwnerArgument<TOwner, TArg, Task<T?>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<T?> GetOrAddAsync<T, TOwner, TArg, TTask>(this ICache<T> cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, DateTimeOffset expiration, CancellationToken token = default)
        where TTask : Task<T?> =>
        cache.GetOrAddAsync<OwnerArgument<TOwner, TArg, Task<T?>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<T?> GetOrAddAsync<T, TOwner, TArg, TTask>(this ICache<T> cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, DateTimeOffset expiration, CancellationToken token = default)
        where TTask : Task<T?>
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddAsync<OwnerArgument<TOwner, TArg, Task<T?>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TOwner, TArg>(this IHashCache cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default) =>
        cache.GetOrAddAsync<T, OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>.Invoke, policy, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TOwner, TArg>(this IHashCache cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddAsync<T, OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>.Invoke, policy, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TOwner, TArg>(this IHashCache cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        cache.GetOrAddAsync<T, OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>.Invoke, expiration, policy, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TOwner, TArg>(this IHashCache cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddAsync<T, OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>.Invoke, expiration, policy, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TOwner, TArg>(this IHashCache cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        cache.GetOrAddAsync<T, OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>.Invoke, expiration, policy, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TOwner, TArg>(this IHashCache cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddAsync<T, OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>.Invoke, expiration, policy, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TOwner, TArg>(this IHashCache cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default) =>
        cache.GetOrAddAsync<T, OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>.Invoke, expiration, setOption, policy, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TOwner, TArg>(this IHashCache cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddAsync<T, OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>.Invoke, expiration, setOption, policy, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TOwner, TArg, TTask>(this IHashCache<T> cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, CancellationToken token = default)
        where TTask : Task<IDictionary<string, T?>> =>
        cache.GetOrAddAsync<OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>.Invoke, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TOwner, TArg, TTask>(this IHashCache<T> cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, CancellationToken token = default)
        where TTask : Task<IDictionary<string, T?>>
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddAsync<OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>.Invoke, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TOwner, TArg, TTask>(this IHashCache<T> cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, TimeSpan expiration, CancellationToken token = default)
        where TTask : Task<IDictionary<string, T?>> =>
        cache.GetOrAddAsync<OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>.Invoke, expiration, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TOwner, TArg, TTask>(this IHashCache<T> cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, TimeSpan expiration, CancellationToken token = default)
        where TTask : Task<IDictionary<string, T?>>
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddAsync<OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>.Invoke, expiration, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TOwner, TArg, TTask>(this IHashCache<T> cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, DateTimeOffset expiration, CancellationToken token = default)
        where TTask : Task<IDictionary<string, T?>> =>
        cache.GetOrAddAsync<OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>.Invoke, expiration, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TOwner, TArg, TTask>(this IHashCache<T> cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, DateTimeOffset expiration, CancellationToken token = default)
        where TTask : Task<IDictionary<string, T?>>
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddAsync<OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<IDictionary<string, T?>>>.Invoke, expiration, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<T?> GetOrAddWithExpirationAsync<T, TOwner, TArg>(this ICache cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default) =>
        cache.GetOrAddWithExpirationAsync<T, OwnerArgument<TOwner, TArg, Task<GeneratedValue<T>>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<GeneratedValue<T>>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<GeneratedValue<T>>>.Invoke, policy, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<T?> GetOrAddWithExpirationAsync<T, TOwner, TArg>(this ICache cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddWithExpirationAsync<T, OwnerArgument<TOwner, TArg, Task<GeneratedValue<T>>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<GeneratedValue<T>>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<GeneratedValue<T>>>.Invoke, policy, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    public static ValueTask<T?> GetOrAddWithExpirationAsync<T, TOwner, TArg>(this ICache<T> cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default) =>
        cache.GetOrAddWithExpirationAsync<OwnerArgument<TOwner, TArg, Task<GeneratedValue<T>>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<GeneratedValue<T>>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<GeneratedValue<T>>>.Invoke, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, so a static lambda needs no closure or tuple.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<T?> GetOrAddWithExpirationAsync<T, TOwner, TArg>(this ICache<T> cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddWithExpirationAsync<OwnerArgument<TOwner, TArg, Task<GeneratedValue<T>>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<GeneratedValue<T>>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<GeneratedValue<T>>>.Invoke, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, as <c>GetOrAddAsync</c>, and reports whether this call generated the value.</summary>
    public static ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TOwner, TArg>(this ICache cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default) =>
        cache.GetOrAddResultAsync<T, OwnerArgument<TOwner, TArg, Task<T?>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, policy, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, as <c>GetOrAddAsync</c>, and reports whether this call generated the value.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TOwner, TArg>(this ICache cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddResultAsync<T, OwnerArgument<TOwner, TArg, Task<T?>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, policy, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, as <c>GetOrAddAsync</c>, and reports whether this call generated the value.</summary>
    public static ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TOwner, TArg>(this ICache cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        cache.GetOrAddResultAsync<T, OwnerArgument<TOwner, TArg, Task<T?>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, policy, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, as <c>GetOrAddAsync</c>, and reports whether this call generated the value.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TOwner, TArg>(this ICache cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddResultAsync<T, OwnerArgument<TOwner, TArg, Task<T?>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, policy, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, as <c>GetOrAddAsync</c>, and reports whether this call generated the value.</summary>
    public static ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TOwner, TArg>(this ICache cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        cache.GetOrAddResultAsync<T, OwnerArgument<TOwner, TArg, Task<T?>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, policy, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, as <c>GetOrAddAsync</c>, and reports whether this call generated the value.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TOwner, TArg>(this ICache cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddResultAsync<T, OwnerArgument<TOwner, TArg, Task<T?>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, policy, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, as <c>GetOrAddAsync</c>, and reports whether this call generated the value.</summary>
    public static ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TOwner, TArg, TTask>(this ICache<T> cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, CancellationToken token = default)
        where TTask : Task<T?> =>
        cache.GetOrAddResultAsync<OwnerArgument<TOwner, TArg, Task<T?>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, as <c>GetOrAddAsync</c>, and reports whether this call generated the value.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TOwner, TArg, TTask>(this ICache<T> cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, CancellationToken token = default)
        where TTask : Task<T?>
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddResultAsync<OwnerArgument<TOwner, TArg, Task<T?>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, as <c>GetOrAddAsync</c>, and reports whether this call generated the value.</summary>
    public static ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TOwner, TArg, TTask>(this ICache<T> cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, TimeSpan expiration, CancellationToken token = default)
        where TTask : Task<T?> =>
        cache.GetOrAddResultAsync<OwnerArgument<TOwner, TArg, Task<T?>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, as <c>GetOrAddAsync</c>, and reports whether this call generated the value.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TOwner, TArg, TTask>(this ICache<T> cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, TimeSpan expiration, CancellationToken token = default)
        where TTask : Task<T?>
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddResultAsync<OwnerArgument<TOwner, TArg, Task<T?>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, token);
    }

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, as <c>GetOrAddAsync</c>, and reports whether this call generated the value.</summary>
    public static ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TOwner, TArg, TTask>(this ICache<T> cache, CacheKey cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, DateTimeOffset expiration, CancellationToken token = default)
        where TTask : Task<T?> =>
        cache.GetOrAddResultAsync<OwnerArgument<TOwner, TArg, Task<T?>>>(cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, Required(generator)), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, token);

    /// <summary>Runs <paramref name="generator"/> on a miss with the owner and argument, as <c>GetOrAddAsync</c>, and reports whether this call generated the value.</summary>
    [OverloadResolutionPriority(2)]
    public static ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TOwner, TArg, TTask>(this ICache<T> cache, ref CacheKeyHandler cacheKey, TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TTask> generator, DateTimeOffset expiration, CancellationToken token = default)
        where TTask : Task<T?>
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        return cache.GetOrAddResultAsync<OwnerArgument<TOwner, TArg, Task<T?>>>(ref cacheKey, new OwnerArgument<TOwner, TArg, Task<T?>>(owner, argument, generator), OwnerArgument<TOwner, TArg, Task<T?>>.Invoke, expiration, token);
    }

    private static TGenerator Required<TGenerator>(TGenerator generator)
        where TGenerator : class
    {
        ArgumentNullException.ThrowIfNull(generator);
        return generator;
    }
}
