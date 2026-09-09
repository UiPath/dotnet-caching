namespace UiPath.Caching;

/// <summary>A key as a log line should show it; nothing is rendered until the logger formats the line, so a disabled level costs one struct copy.</summary>
internal readonly struct LoggedKey
{
    private readonly KeyMasker _masker;
    private readonly string? _key;
    private readonly RedisKey _composed;
    private readonly bool _hasComposed;
    private readonly Type? _valueType;

    private LoggedKey(KeyMasker masker, string? key, RedisKey composed, bool hasComposed, Type? valueType)
    {
        _masker = masker;
        _key = key;
        _composed = composed;
        _hasComposed = hasComposed;
        _valueType = valueType;
    }

    public static LoggedKey For(KeyMasker masker, CacheKey key, Type? valueType = null) =>
        new(masker, key.Name ?? string.Empty, default, hasComposed: false, valueType);

    /// <summary>Logged as the key strategy composed it.</summary>
    public static LoggedKey For(KeyMasker masker, CacheKey key, RedisKey composed, Type? valueType = null) =>
        new(masker, key.Name ?? string.Empty, composed, hasComposed: true, valueType);

    /// <summary>For a call site that only has the composed key; it is judged, and masked, whole.</summary>
    public static LoggedKey Composed(KeyMasker masker, RedisKey composed, Type? valueType = null) =>
        new(masker, key: null, composed, hasComposed: true, valueType);

    /// <summary>A key known to be the consumer's, such as an <c>IDistributedCache</c> key.</summary>
    public static LoggedKey Secret(string key) =>
        new(KeyMasker.Always, key, default, hasComposed: false, valueType: null);

    public override string ToString()
    {
        var composed = _hasComposed ? _composed.ToString() : null;
        return _masker.Render(_key ?? composed ?? string.Empty, composed, _valueType);
    }
}

/// <summary>The same, for the log lines that name several keys at once.</summary>
internal readonly struct LoggedKeys
{
    private readonly KeyMasker _masker;
    private readonly IReadOnlyCollection<CacheKey>? _keys;
    private readonly IReadOnlyCollection<CacheEntryOptions>? _entries;
    private readonly Type? _valueType;

    public LoggedKeys(KeyMasker masker, IReadOnlyCollection<CacheKey> keys, Type? valueType = null)
    {
        _masker = masker;
        _keys = keys;
        _valueType = valueType;
    }

    /// <summary>Shows each composed key but judges, and masks, the caller's own key inside it.</summary>
    public LoggedKeys(KeyMasker masker, IReadOnlyCollection<CacheEntryOptions> entries, Type? valueType = null)
    {
        _masker = masker;
        _entries = entries;
        _valueType = valueType;
    }

    public override string ToString()
    {
        var masker = _masker;
        var valueType = _valueType;
        return _entries is not null
            ? string.Join(',', _entries.Select(e => masker.Render(e.CallerKey.Name ?? string.Empty, e.CacheKey.Name, valueType)))
            : string.Join(',', (_keys ?? []).Select(key => masker.Render(key.Name ?? string.Empty, composed: null, valueType)));
    }
}
