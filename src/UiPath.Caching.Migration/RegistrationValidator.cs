using System.Globalization;
using System.Reflection;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration;

/// <summary>
/// The library settings of the router's stack that would change what the application's own client sees, checked when the router is built.
/// Every setting the router depends on has a rule here; a library option added later is caught by the test that pins the option list.
/// </summary>
internal static class RegistrationValidator
{
    /// <summary>Every broken rule, one line each; empty when the stack matches the source.</summary>
    public static IReadOnlyList<string> Violations(CacheOptions cacheOptions, RedisCacheOptions redisOptions)
    {
        var violations = new List<string>();
        if (redisOptions.RedisKeyStrategyFactory is not PassThroughKeyStrategyFactory)
        {
            violations.Add($"Redis.{nameof(RedisCacheOptions.RedisKeyStrategyFactory)} is replaced: the router's keys are the source's key text, verbatim.");
        }

        if (redisOptions.KeyFormat is not null)
        {
            violations.Add($"Redis.{nameof(RedisCacheOptions.KeyFormat)} is set: the router's keys are the source's key text, verbatim.");
        }

        if (redisOptions.CacheKeyStrategy is not null)
        {
            violations.Add($"Redis.{nameof(RedisCacheOptions.CacheKeyStrategy)} is set: it would transform the source's key text.");
        }

        if (redisOptions.EntryFactory is not null)
        {
            violations.Add($"Redis.{nameof(RedisCacheOptions.EntryFactory)} is set: the entry must be the value the source writes.");
        }

        if (redisOptions.RemoveUnreadable)
        {
            violations.Add($"Redis.{nameof(RedisCacheOptions.RemoveUnreadable)} is on: a value only the source can read would be deleted.");
        }

        if (redisOptions.ReadFrom != RedisReadFrom.Master)
        {
            violations.Add($"Redis.{nameof(RedisCacheOptions.ReadFrom)} is {redisOptions.ReadFrom}: reads go to the primary, as the source's do.");
        }

        // The router's provider is Redis-only, passes absolute deadlines and awaits its writes, so jitter, fail-safe and background writes never reach it.
        if (cacheOptions.DefaultCachePolicy?.Serializer is not null)
        {
            violations.Add($"{nameof(CacheOptions.DefaultCachePolicy)}.{nameof(CachePolicy.Serializer)} is set: values are written with the source's envelope only.");
        }

        return violations;
    }

    public static void ThrowIfInvalid(CacheOptions cacheOptions, RedisCacheOptions redisOptions)
    {
        if (Violations(cacheOptions, redisOptions) is { Count: > 0 } violations)
        {
            throw new InvalidOperationException(
                "The router's library stack has settings that would change what the application's own client sees. Remove them from the router's configuration section: "
                + string.Join(" ", violations));
        }
    }

    /// <summary>The value of every scalar option of the stack, for the startup log.</summary>
    public static string Describe(CacheOptions cacheOptions, RedisCacheOptions redisOptions) =>
        string.Join("; ", Values("Redis", redisOptions).Concat(Values("Redis.FormatNegotiation", redisOptions.FormatNegotiation)).Concat(Values("Caching", cacheOptions)));

    internal static IEnumerable<string> Values(string prefix, object options) =>
        options.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{prefix}.{property.Name}={Render(property.GetValue(options))}");

    private static string Render(object? value) => value switch
    {
        null => "none",
        string text => text,
        Uri uri => uri.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        System.Collections.ICollection collection => $"{collection.Count} entries",
        _ => value.GetType().Name,
    };
}
