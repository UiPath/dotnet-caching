namespace UiPath.Caching;

/// <summary>Runs a closure-form batch generator passed as the argument of the argument form, so both share one path.</summary>
internal static class DirectBatch<T, TState>
    where TState : notnull
{
    public static readonly Func<Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>>, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> Invoke =
        static (generator, states, token) => generator(states, token);
}
