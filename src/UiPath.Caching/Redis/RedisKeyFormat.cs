using System.Globalization;
using System.Text;

namespace UiPath.Caching.Redis;

/// <summary>The join of a Redis key, <c>{app}{sep}{keyspace}{sep}{name}</c>: the prefix is lowercased and the name kept verbatim.</summary>
public sealed class RedisKeyFormat
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private RedisKeyFormat(string id, char separator, bool shardKey)
    {
        Id = id;
        Separator = separator;
        ShardKey = shardKey;
    }

    public static RedisKeyFormat Colon { get; } = new("1", ':', shardKey: false);

    public static RedisKeyFormat Dollar { get; } = new("2", '$', shardKey: false);

    public string Id { get; }

    public char Separator { get; }

    /// <summary>Wraps a name that carries no hash tag in one, so every key of the name sits in one cluster slot.</summary>
    public bool ShardKey { get; }

    public static RedisKeyFormat Create(char separator, bool shardKey)
    {
        var format = Derive(separator, shardKey);
        if (format.Separator is '{' or '}')
        {
            throw new ArgumentException("A key format separator cannot be a brace.", nameof(separator));
        }

        return format;
    }

    public RedisKey Build(string app, string keyspace, string name) =>
        BuildName(Prefix(app, keyspace), name);

    /// <summary>True only for a key this format builds: the exact prefix, then a name that <see cref="Build"/> turns back into the same bytes.</summary>
    public bool TryParse(RedisKey key, string app, string keyspace, [NotNullWhen(true)] out string? name)
    {
        name = null;
        var bytes = (byte[]?)key;
        var prefix = (byte[])Prefix(app, keyspace)!;
        if (bytes is null || !bytes.AsSpan().StartsWith(prefix))
        {
            return false;
        }

        string rest;
        try
        {
            rest = StrictUtf8.GetString(bytes, prefix.Length, bytes.Length - prefix.Length);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (ShardKey && !RedisHashTag.HasValidTag(rest))
        {
            return false;
        }

        name = rest;
        return true;
    }

    /// <summary>The strategy for one app and keyspace. Refuses an app name with braces and a format whose prefixes over the library keyspaces overlap.</summary>
    public IRedisKeyStrategy CreateStrategy(string app, string keyspace)
    {
        RedisKeyFormatRules.EnsurePrefixFree(
            Guard.NotNullOrWhiteSpace(app, nameof(app)),
            [this],
            RedisKeyFormatRules.Keyspaces.Contains(keyspace, StringComparer.Ordinal) ? RedisKeyFormatRules.Keyspaces : [.. RedisKeyFormatRules.Keyspaces, keyspace]);
        return new FormatRedisKeyStrategy(this, app, keyspace);
    }

    /// <summary>The format today's options imply. Unlike <see cref="Create"/> it takes any separator they accept, braces included.</summary>
    internal static RedisKeyFormat Derive(char separator, bool shardKey)
    {
        var normalized = char.ToLowerInvariant(Guard.NotWhiteSpace(separator, nameof(separator)));
        if (char.IsSurrogate(normalized))
        {
            throw new ArgumentException("Value cannot be a surrogate.", nameof(separator));
        }

        return (normalized, shardKey) switch
        {
            (':', false) => Colon,
            ('$', false) => Dollar,
            (':', true) => new("1t", ':', shardKey: true),
            ('$', true) => new("2t", '$', shardKey: true),
            _ => new(
                string.Create(CultureInfo.InvariantCulture, $"u{(int)normalized:x4}{(shardKey ? "t" : string.Empty)}"),
                normalized,
                shardKey),
        };
    }

    internal static RedisKeyFormat Derive(CacheOptions options)
    {
#pragma warning disable CS0618 // Still honored; see CacheOptions.ShardKeyEnabled.
        return Derive(options.Separator, options.ShardKeyEnabled);
#pragma warning restore CS0618
    }

    internal RedisKey Prefix(string app, string keyspace)
    {
        var joined = string.Join(
            Separator,
            Guard.NotNullOrWhiteSpace(app, nameof(app)),
            Guard.NotNullOrWhiteSpace(keyspace, nameof(keyspace))).ToLowerInvariant();
        return Encoding.UTF8.GetBytes($"{joined}{Separator}");
    }

    internal RedisKey BuildName(RedisKey prefix, string name) =>
        ((RedisKey)(ShardKey ? RedisHashTag.EnsureTag(name, nameof(RedisKeyFormat)) : name)).Prepend(prefix);
}
