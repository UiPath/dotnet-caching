namespace UiPath.Caching;

/// <summary>The same, for the log lines that name several keys at once.</summary>
internal readonly struct LoggedKeys
{
    private readonly KeyMasker _masker;
    private readonly IReadOnlyCollection<CacheKey>? _keys;
    private readonly IReadOnlyCollection<CacheEntryOptions>? _entries;
    private readonly Type? _valueType;
    private readonly bool _composedOnly;

    public LoggedKeys(KeyMasker masker, IReadOnlyCollection<CacheKey> keys, Type? valueType = null, bool composedOnly = false)
    {
        _masker = masker;
        _keys = keys;
        _valueType = valueType;
        _composedOnly = composedOnly;
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
        if (_entries is not null)
        {
            return string.Join(',', _entries.Select(e => masker.Render(e.CallerKey.Name ?? string.Empty, e.CacheKey.Name, valueType)));
        }

        var composedOnly = _composedOnly;
        return string.Join(',', (_keys ?? []).Select(key => composedOnly
            ? masker.Render(key: null, key.Name, valueType)
            : masker.Render(key.Name ?? string.Empty, composed: null, valueType)));
    }
}
