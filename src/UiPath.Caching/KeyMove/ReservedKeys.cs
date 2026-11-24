using System.Text.RegularExpressions;

namespace UiPath.Caching.KeyMove;

/// <summary>The keys of the move itself (state, leases, control records, fences, staging), which no registration may own.</summary>
internal static partial class ReservedKeys
{
    /// <summary>True for a control, state, lease or fence key under the library prefix or the bare keyspace, and for a staging key; checked before any ownership delegate runs.</summary>
    public static bool IsReserved(string libraryPrefix, string key) =>
        key.StartsWith(MoveKeyNames.Keyspace + ":", StringComparison.Ordinal)
        || key.StartsWith(libraryPrefix + MoveKeyNames.Keyspace + ":", StringComparison.Ordinal)
        || Staging().IsMatch(key);

    /// <summary>The data key a staging key was made for, when <paramref name="key"/> is one.</summary>
    public static bool TryStagedKey(string key, out string dataKey)
    {
        var match = Staging().Match(key);
        dataKey = match.Success ? key[..match.Index] : string.Empty;
        return match.Success;
    }

    [GeneratedRegex(":stg:[0-9a-f]{32}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Staging();
}
