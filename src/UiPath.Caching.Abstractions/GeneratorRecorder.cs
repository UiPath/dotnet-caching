namespace UiPath.Caching;

/// <summary>Records that a generator ran, for a cache that reports whether a call generated its value without a path of its own.</summary>
internal sealed class GeneratorRecorder<T>
{
    private readonly Func<CancellationToken, Task<T?>> _generator;

    private GeneratorRecorder(Func<CancellationToken, Task<T?>> generator)
    {
        _generator = generator;
        Generator = Run;
    }

    /// <summary>The wrapped generator; passing it to the cache marks the recorder when it runs.</summary>
    public Func<CancellationToken, Task<T?>> Generator { get; }

    public bool Ran { get; private set; }

    public static GeneratorRecorder<T> For(Func<CancellationToken, Task<T?>> generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return new GeneratorRecorder<T>(generator);
    }

    public static GeneratorRecorder<T> For<TState>(TState state, Func<TState, CancellationToken, Task<T?>> generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return new GeneratorRecorder<T>(token => generator(state, token));
    }

    /// <param name="pending">The call's outcome.</param>
    /// <param name="expiration">What the entry expires at when the generator ran and the caller set it; a hit's expiration is unknown here.</param>
    public async ValueTask<CacheResult<T>> ResultOf(ValueTask<T?> pending, DateTimeOffset? expiration = null)
    {
        var value = await pending.ConfigureAwait(false);
        return new CacheResult<T>(value, Ran ? expiration : null, Ran);
    }

    private Task<T?> Run(CancellationToken token)
    {
        Ran = true;
        return _generator(token);
    }
}
