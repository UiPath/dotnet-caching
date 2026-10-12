namespace UiPath.Caching;

/// <summary>What a generator returns when the value knows its own lifetime; a null <paramref name="Expiration"/> leaves it to the policy.</summary>
public readonly record struct GeneratedValue<T>(T? Value, DateTimeOffset? Expiration = null);
