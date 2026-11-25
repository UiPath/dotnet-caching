namespace UiPath.Caching.Migration;

/// <summary>The one rule of every member of <see cref="ICache{T}"/> that a routed cache implements; the sealed members forward to these.</summary>
internal static class CacheMemberRules
{
    private const string ExpiryReadRule = "The library's own expiry-only read (a key move state answers from its union read); the source reports none.";

    private const string GetOrAddRule = "A routed read, then the generator under single flight, then a fill that never clobbers a newer value.";

    private const string GetOrAddManyRule = "Routed reads, one generator call for the keys that missed, and each made value stored by the fill of a single get-or-add.";

    private const string GetOrAddWithExpirationRule = "As get-or-add, with the life taken from the generated value after the generator ran.";

    private const string RefreshRule = "The library refreshes where the key lives; a group in a key move state has no checked refresh and refuses it; the source has none.";

    private const string SetRule = "A routed write with the write forms of the state; true when it committed; a null value removes, as the library's set does.";

    private const string SetManyRule = "One routed write per key, together; true when every one committed.";

    private const string SetIfChangedRule = "The library's atomic compare-and-swap (the exact bytes are compared by script or transaction); a key move state has no checked form of it and refuses it, and the source has none.";

    private const string TryAddRule = "A conditional create: the library's, or the key move forms; the source has none. As the library's, false for a null value and for a create that was not sent or has no known outcome.";

    private const string TryFillRule = "The library's own fill: written only when absent, nothing announced, the winner's value and expiration returned when it lost; a key move state fills by its own form; the source has no fill.";

    public static IReadOnlyDictionary<string, CacheMemberRule> Table { get; } = new Dictionary<string, CacheMemberRule>(StringComparer.Ordinal)
    {
        ["ContainsAsync(CacheKey, CancellationToken)"] = new(RuleClass.Routed, false, "A routed read: the local tier, then the active path; any source can say whether a key exists."),
        ["ExpireTimeAsync(CacheKey, CancellationToken)"] = new(RuleClass.Backend, true, ExpiryReadRule),
        ["GetAsync(CacheKey, CancellationToken)"] = new(RuleClass.Routed, false, "A routed read with the local tier, the fallback rules and, in a key move state, the union read and the promotion."),
        ["GetAsync(CacheKey[], CancellationToken)"] = new(RuleClass.Routed, false, "One routed read per key, together; the key move batch read is not used here."),
        ["GetOrAddAsync(CacheKey, Func<CancellationToken,Task<T>>, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddRule),
        ["GetOrAddAsync(CacheKey, Func<CancellationToken,Task<T>>, DateTimeOffset, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddRule),
        ["GetOrAddAsync(CacheKey, Func<CancellationToken,Task<T>>, TimeSpan, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddRule),
        ["GetOrAddAsync<TState,TArg>(KeyValuePair<CacheKey,TState>[], TArg, Func<TArg,TState[],CancellationToken,Task<KeyValuePair<TState,T>[]>>, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddManyRule),
        ["GetOrAddAsync<TState,TArg>(KeyValuePair<CacheKey,TState>[], TArg, Func<TArg,TState[],CancellationToken,Task<KeyValuePair<TState,T>[]>>, DateTimeOffset, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddManyRule),
        ["GetOrAddAsync<TState,TArg>(KeyValuePair<CacheKey,TState>[], TArg, Func<TArg,TState[],CancellationToken,Task<KeyValuePair<TState,T>[]>>, TimeSpan, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddManyRule),
        ["GetOrAddAsync<TState>(CacheKey, TState, Func<TState,CancellationToken,Task<T>>, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddRule),
        ["GetOrAddAsync<TState>(CacheKey, TState, Func<TState,CancellationToken,Task<T>>, DateTimeOffset, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddRule),
        ["GetOrAddAsync<TState>(CacheKey, TState, Func<TState,CancellationToken,Task<T>>, TimeSpan, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddRule),
        ["GetOrAddAsync<TState>(KeyValuePair<CacheKey,TState>[], Func<TState[],CancellationToken,Task<KeyValuePair<TState,T>[]>>, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddManyRule),
        ["GetOrAddAsync<TState>(KeyValuePair<CacheKey,TState>[], Func<TState[],CancellationToken,Task<KeyValuePair<TState,T>[]>>, DateTimeOffset, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddManyRule),
        ["GetOrAddAsync<TState>(KeyValuePair<CacheKey,TState>[], Func<TState[],CancellationToken,Task<KeyValuePair<TState,T>[]>>, TimeSpan, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddManyRule),
        ["GetOrAddWithExpirationAsync(CacheKey, Func<CancellationToken,Task<GeneratedValue<T>>>, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddWithExpirationRule),
        ["GetOrAddWithExpirationAsync<TState>(CacheKey, TState, Func<TState,CancellationToken,Task<GeneratedValue<T>>>, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddWithExpirationRule),
        ["Name"] = new(RuleClass.Local, false, "The router's registration name."),
        ["RefreshAsync(CacheKey, CancellationToken)"] = new(RuleClass.Backend, true, RefreshRule),
        ["RefreshAsync(CacheKey, DateTimeOffset, CancellationToken)"] = new(RuleClass.Backend, true, RefreshRule),
        ["RefreshAsync(CacheKey, TimeSpan, CancellationToken)"] = new(RuleClass.Backend, true, RefreshRule),
        ["RemoveAsync(CacheKey, CancellationToken)"] = new(RuleClass.Routed, false, "A routed delete with the write forms of the state; true when it committed."),
        ["RemoveAsync(CacheKey[], CancellationToken)"] = new(RuleClass.Routed, false, "One routed delete per key, together; true when every one committed."),
        ["SetAsync(CacheKey, T, CancellationToken)"] = new(RuleClass.Routed, false, SetRule),
        ["SetAsync(CacheKey, T, DateTimeOffset, CancellationToken)"] = new(RuleClass.Routed, false, SetRule),
        ["SetAsync(CacheKey, T, TimeSpan, CancellationToken)"] = new(RuleClass.Routed, false, SetRule),
        ["SetAsync(KeyValuePair<CacheKey,T>[], CancellationToken)"] = new(RuleClass.Routed, false, SetManyRule),
        ["SetAsync(KeyValuePair<CacheKey,T>[], DateTimeOffset, CancellationToken)"] = new(RuleClass.Routed, false, SetManyRule),
        ["SetAsync(KeyValuePair<CacheKey,T>[], TimeSpan, CancellationToken)"] = new(RuleClass.Routed, false, SetManyRule),
        ["SetIfChangedAsync(CacheKey, T, IEqualityComparer<T>, DateTimeOffset, CancellationToken)"] = new(RuleClass.Backend, true, SetIfChangedRule),
        ["SetIfChangedAsync(CacheKey, T, IEqualityComparer<T>, TimeSpan, CancellationToken)"] = new(RuleClass.Backend, true, SetIfChangedRule),
        ["TimeToLiveAsync(CacheKey, CancellationToken)"] = new(RuleClass.Backend, true, ExpiryReadRule),
        ["TryAddAsync(CacheKey, T, CancellationToken)"] = new(RuleClass.Routed, true, TryAddRule),
        ["TryAddAsync(CacheKey, T, DateTimeOffset, CancellationToken)"] = new(RuleClass.Routed, true, TryAddRule),
        ["TryAddAsync(CacheKey, T, TimeSpan, CancellationToken)"] = new(RuleClass.Routed, true, TryAddRule),
        ["TryFillAsync(CacheKey, T, DateTimeOffset, CancellationToken)"] = new(RuleClass.Backend, true, TryFillRule),
        ["TryFillAsync(CacheKey, T, TimeSpan, CancellationToken)"] = new(RuleClass.Backend, true, TryFillRule),
    };
}
