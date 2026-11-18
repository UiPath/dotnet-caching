namespace UiPath.Caching;

/// <summary>How <see cref="CacheKey"/> normalizes its name at construction; comparison is always ordinal.</summary>
public enum CacheKeyCasing
{
    /// <summary>Trim and lowercase (invariant). The historical default.</summary>
    Insensitive = 0,

    /// <summary>Trim only; the caller's casing is preserved.</summary>
    Sensitive = 1,

    /// <summary>The name exactly as given, set per key by <see cref="CacheKey.Verbatim(string)"/>; not valid for <see cref="CacheOptions.KeyCasing"/>.</summary>
    Verbatim = 2,
}
