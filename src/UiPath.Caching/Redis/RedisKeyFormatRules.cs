namespace UiPath.Caching.Redis;

/// <summary>Checks a pair of key formats must pass before keys move between them.</summary>
internal static class RedisKeyFormatRules
{
    public const string SetKeyspace = "se";

    public const string ControlKeyspace = "mg";

    public const string ClaimKeyspace = "mx";

    private static readonly char[] VersionRefused = [CacheOptions.KeySeparator, '+', '@', '=', '{', '}'];

    /// <summary>Every keyspace whose prefixes must stay apart: the library's own plus the move's control and claim keyspaces.</summary>
    public static IReadOnlyList<string> Keyspaces { get; } =
    [
        RedisKeyspaces.String, RedisKeyspaces.Hash, SetKeyspace, RedisKeyspaces.PubSub, RedisKeyspaces.Streams, ControlKeyspace, ClaimKeyspace,
    ];

    public static void EnsureNoBraces(string app, RedisKeyFormat format)
    {
        if (app.AsSpan().IndexOfAny('{', '}') >= 0 || format.Separator is '{' or '}')
        {
            throw new InvalidOperationException($"The app short name '{app}' and the separator of key format '{format.Id}' cannot contain braces.");
        }
    }

    /// <summary>No prefix equals or starts another of a different (format, keyspace) pair, and one prefix never has two tag modes.</summary>
    public static void EnsurePrefixFree(string app, IReadOnlyCollection<RedisKeyFormat> formats, IReadOnlyCollection<string> keyspaces)
    {
        var prefixes = new List<(RedisKeyFormat Format, string Keyspace, byte[] Bytes)>();
        foreach (var format in formats)
        {
            EnsureNoBraces(app, format);
            foreach (var keyspace in keyspaces)
            {
                prefixes.Add((format, keyspace, (byte[])format.Prefix(app, keyspace)!));
            }
        }

        for (var i = 0; i < prefixes.Count; i++)
        {
            for (var j = 0; j < prefixes.Count; j++)
            {
                var (a, b) = (prefixes[i], prefixes[j]);
                if (i == j || (a.Format.Id == b.Format.Id && a.Keyspace == b.Keyspace) || !b.Bytes.AsSpan().StartsWith(a.Bytes))
                {
                    continue;
                }

                var reason = a.Bytes.Length == b.Bytes.Length && a.Format.ShardKey != b.Format.ShardKey
                    ? "share a prefix with different tag modes"
                    : "have prefixes where one starts the other";
                throw new InvalidOperationException(
                    $"Key format '{a.Format.Id}' keyspace '{a.Keyspace}' and key format '{b.Format.Id}' keyspace '{b.Keyspace}' {reason}.");
            }
        }
    }

    /// <summary>A reserved keyspace outside <see cref="Keyspaces"/> may hold keys built from the format, so it moves only when attested as pinned.</summary>
    public static void EnsureKeyspacesSupported(IEnumerable<IReservedRedisKeyspace> reserved, IReadOnlyCollection<string> pinned)
    {
        foreach (var keyspace in reserved)
        {
            if (!Keyspaces.Contains(keyspace.Keyspace, StringComparer.Ordinal) && !pinned.Contains(keyspace.Keyspace, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"unsupported_keyspace: '{keyspace.Keyspace}' ({keyspace.Owner}) is not a library keyspace. Name it in the pinned keyspaces if its keys are not built from the key format.");
            }
        }
    }

    /// <summary>A string or hash entry of the format, excluding keys the library names for itself (node list, maintainer lock and quarantine, staging).</summary>
    public static bool IsMovable(RedisKeyFormat format, RedisKey key, string app, string keyspace, char nameSeparator)
    {
        if ((keyspace != RedisKeyspaces.String && keyspace != RedisKeyspaces.Hash) || !format.TryParse(key, app, keyspace, out var name))
        {
            return false;
        }

        name = LogicalName(format, name);

        // Both go through CacheKey, so they carry the casing in force when the library built them.
        return !name.Equals(LibraryKeyNames.NodeList(nameSeparator), StringComparison.OrdinalIgnoreCase)
            && !name.Equals(LibraryKeyNames.MaintainerLock(nameSeparator), StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith(KeyEscaping.EscapeBraces(LibraryKeyNames.QuarantineMarker(nameSeparator)), StringComparison.Ordinal)
            && !name.Contains(":stg:", StringComparison.Ordinal);
    }

    public static void EnsureValidVersion(string version)
    {
        if (string.IsNullOrEmpty(version) || version.AsSpan().IndexOfAny(VersionRefused) >= 0)
        {
            throw new ArgumentException(
                $"A cache version must be non-empty and cannot contain '{string.Concat(VersionRefused)}'.", nameof(version));
        }
    }

    /// <summary>A name is live when it starts with one of the live versions and the key separator; anything else is a dead version.</summary>
    public static bool IsLiveVersion(RedisKeyFormat format, string parsedName, IEnumerable<string> liveVersions)
    {
        var name = LogicalName(format, parsedName);
        foreach (var version in liveVersions)
        {
            if (name.Length > version.Length
                && name[version.Length] == CacheOptions.KeySeparator
                && name.StartsWith(version, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A parsed name without the tag a sharded format wrapped around it: <see cref="RedisKeyFormat.Build"/> of either gives the same key.</summary>
    internal static string LogicalName(RedisKeyFormat format, string parsedName) =>
        format.ShardKey && parsedName.Length > 2 && parsedName[0] == '{' && parsedName[^1] == '}' && RedisHashTag.ContainsNoBraces(parsedName[1..^1])
            ? parsedName[1..^1]
            : parsedName;
}
