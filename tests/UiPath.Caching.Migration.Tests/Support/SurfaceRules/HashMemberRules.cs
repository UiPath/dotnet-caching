namespace UiPath.Caching.Migration;

/// <summary>The one rule of every member of <see cref="IHashCache{T}"/> that a routed hash cache implements; the span-key members and the sealed members forward to these.</summary>
internal static class HashMemberRules
{
    private const string SetNote = "A routed write with the write forms of the state: a replace of the key, or a merge of the fields with the hash option; true when it committed; metadata is refused on the source and never falls back to it.";

    private const string RefreshNote = "The library renews where the hash lives; the options form replaces the metadata and null clears it; a key move state acknowledges only a renewal that lands on its native side; the source has none.";

    private const string GetOrAddNote = "A routed read, then the generator under single flight, then a whole-hash fill that never clobbers a newer hash; an empty hash is stored nowhere.";

    public static IReadOnlyDictionary<string, CacheMemberRule> Table { get; } = new Dictionary<string, CacheMemberRule>(StringComparer.Ordinal)
    {
        ["ContainsAsync(CacheKey, CancellationToken)"] = new(RuleClass.Routed, false, "The active client says whether the hash is there when it can, and a routed read answers when it cannot."),
        ["ExpireTimeAsync(CacheKey, CancellationToken)"] = new(RuleClass.Backend, true, "The library's expiry of the hash (a key move state answers from its holder's life); the time to live is the expiry less now, never below zero; the source reports none."),
        ["GetAsync(CacheKey, CancellationToken)"] = new(RuleClass.Routed, false, "A routed read of the whole hash or of the fields asked for; a miss is an empty hash, or null for one field."),
        ["GetAsync(CacheKey, String[], CancellationToken)"] = new(RuleClass.Routed, false, "A routed read of the whole hash or of the fields asked for; a miss is an empty hash, or null for one field."),
        ["GetCacheEntryAsync(CacheKey, CancellationToken)"] = new(RuleClass.Routed, false, "A routed read of the whole hash, as an entry with its expiry and metadata; a miss is an empty hash that is not found."),
        ["GetItemAsync(CacheKey, String, CancellationToken)"] = new(RuleClass.Routed, false, "A routed read of the whole hash or of the fields asked for; a miss is an empty hash, or null for one field."),
        ["GetMetadataAsync(CacheKey, CancellationToken)"] = new(RuleClass.Routed, false, "A routed read of the whole hash that answers its metadata; the source holds none."),
        ["GetOrAddAsync(CacheKey, Func<CancellationToken,Task<IDictionary<String,T>>>, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddNote),
        ["GetOrAddAsync(CacheKey, Func<CancellationToken,Task<IDictionary<String,T>>>, DateTimeOffset, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddNote),
        ["GetOrAddAsync(CacheKey, Func<CancellationToken,Task<IDictionary<String,T>>>, TimeSpan, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddNote),
        ["GetOrAddAsync<TState>(CacheKey, TState, Func<TState,CancellationToken,Task<IDictionary<String,T>>>, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddNote),
        ["GetOrAddAsync<TState>(CacheKey, TState, Func<TState,CancellationToken,Task<IDictionary<String,T>>>, DateTimeOffset, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddNote),
        ["GetOrAddAsync<TState>(CacheKey, TState, Func<TState,CancellationToken,Task<IDictionary<String,T>>>, TimeSpan, CancellationToken)"] = new(RuleClass.Routed, false, GetOrAddNote),
        ["Name"] = new(RuleClass.Local, false, "The router's registration name."),
        ["RefreshAsync(CacheKey, CancellationToken)"] = new(RuleClass.Backend, true, RefreshNote),
        ["RefreshAsync(CacheKey, DateTimeOffset, CancellationToken)"] = new(RuleClass.Backend, true, RefreshNote),
        ["RefreshAsync(CacheKey, HashCacheEntryOptions, CancellationToken)"] = new(RuleClass.Backend, true, RefreshNote),
        ["RefreshAsync(CacheKey, TimeSpan, CancellationToken)"] = new(RuleClass.Backend, true, RefreshNote),
        ["RemoveAsync(CacheKey, CancellationToken)"] = new(RuleClass.Routed, false, "A routed delete of the whole hash with the write forms of the state; true when it committed."),
        ["SetAsync(CacheKey, IDictionary<String,T>, CancellationToken)"] = new(RuleClass.Routed, false, SetNote),
        ["SetAsync(CacheKey, IDictionary<String,T>, DateTimeOffset, CancellationToken)"] = new(RuleClass.Routed, false, SetNote),
        ["SetAsync(CacheKey, IDictionary<String,T>, HashCacheEntryOptions, CancellationToken)"] = new(RuleClass.Routed, false, SetNote),
        ["SetAsync(CacheKey, IDictionary<String,T>, TimeSpan, CancellationToken)"] = new(RuleClass.Routed, false, SetNote),
        ["SetMetadataAsync(CacheKey, IDictionary<String,String>, CancellationToken)"] = new(RuleClass.Backend, true, "Refused on every path: no checked form of a metadata-only write exists yet."),
        ["TimeToLiveAsync(CacheKey, CancellationToken)"] = new(RuleClass.Backend, true, "The library's expiry of the hash (a key move state answers from its holder's life); the time to live is the expiry less now, never below zero; the source reports none."),
    };
}
