namespace UiPath.Caching.Config;

/// <summary>
/// Refuses configuration still written under an options key 2.0 removed. The binder skips a key it
/// cannot place without a word, so a leftover alias would quietly turn into the default: an L1 cap
/// gone, not renamed.
/// </summary>
internal static class RemovedConfigurationKeys
{
    internal static readonly IReadOnlyList<(string Old, string? New)> Multilayer =
    [
        ("PrimaryMaxExpiration", "LocalMaxExpiration"),
        ("PrimaryMaxExpirationDisconnected", "LocalMaxExpirationDisconnected"),
        ("UsePrimaryOnlyWhenDisconnected", "UseLocalOnlyWhenDisconnected"),
    ];

    internal static readonly IReadOnlyList<(string Old, string? New)> RedisConnection =
    [
        ("ThreadPoolSocketManager", null),
    ];

    /// <exception cref="InvalidOperationException">A removed key is present under <paramref name="section"/>.</exception>
    internal static void ThrowIfPresent(IConfigurationSection section, IReadOnlyList<(string Old, string? New)> removed)
    {
        // The children are enumerated rather than probed with Exists(), which is false for a key whose
        // value is null. JSON keeps "Key": null as a key with no value, and in 1.x binding that null was
        // how a caller removed the default cap, so it has to be caught too.
        var present = new HashSet<string>(section.GetChildren().Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
        List<string>? found = null;
        foreach (var (old, replacement) in removed)
        {
            if (!present.Contains(old))
            {
                continue;
            }

            found ??= [];
            found.Add(replacement is null
                ? $"'{old}' was removed and has no effect; delete it"
                : $"'{old}' was renamed; use '{replacement}'");
        }

        if (found is null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Configuration section '{section.Path}' uses keys that UiPath.Caching 2.0 removed. The binder would " +
            $"ignore them without error, so the values would silently fall back to their defaults: " +
            string.Join("; ", found) + ".");
    }
}
