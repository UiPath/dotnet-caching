using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>Names of a move's fences, control records and staging keys: logical names, physical slots, so each name lands in its data key's slot.</summary>
internal sealed class MoveKeyNames
{
    public const string Keyspace = "mg";

    private static readonly char[] ForbiddenNameCharacters = ['+', '@', '=', '{', '}'];

    private readonly int? _prefixSlot;

    /// <param name="connectorPrefix">The prefix the connection prepends to every key; empty for none.</param>
    /// <param name="libraryPrefix">The library key prefix that starts every name carrying no data key, so two deployments on one server never share them.</param>
    /// <param name="cluster">False names one control record per group, with no tags.</param>
    public MoveKeyNames(string connectorPrefix, string libraryPrefix, bool cluster)
    {
        ConnectorPrefix = connectorPrefix ?? throw new ArgumentNullException(nameof(connectorPrefix));
        LibraryPrefix = libraryPrefix ?? throw new ArgumentNullException(nameof(libraryPrefix));
        if (!RedisHashTag.ContainsNoBraces(libraryPrefix))
        {
            throw new ArgumentException($"The library key prefix '{libraryPrefix}' may not hold a brace: it comes before the slot tag of every name.", nameof(libraryPrefix));
        }

        Cluster = cluster;
        if (cluster && PrefixTag(connectorPrefix))
        {
            _prefixSlot = HashSlot.Of(connectorPrefix);
        }
    }

    public string ConnectorPrefix { get; }

    public string LibraryPrefix { get; }

    public bool Cluster { get; }

    /// <summary>Group and cache names may not hold the characters the names and gate fields are built with.</summary>
    public static void ValidateName(string name, string paramName)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(ForbiddenNameCharacters) >= 0)
        {
            throw new ArgumentException($"'{name}' cannot take part in a move: a name must be non-empty and hold none of + @ = {{ }}.", paramName);
        }
    }

    /// <summary>A key text may not hold an empty brace pair, nor, on a cluster, an unmatched brace.</summary>
    public static bool IsValidKeyText(string key, bool cluster)
    {
        if (key.Contains("{}", StringComparison.Ordinal))
        {
            return false;
        }

        if (!cluster)
        {
            return true;
        }

        var depth = 0;
        foreach (var c in key)
        {
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    /// <summary>The slot of the physical key.</summary>
    public int SlotOf(string logicalKey) => HashSlot.Of(ConnectorPrefix, logicalKey);

    /// <summary>G(K): the forward fence, in the native key's slot.</summary>
    public RedisKey ForwardFence(string nativeKey) => SameSlot(nativeKey, Keyspace + ":" + nativeKey);

    /// <summary>GL(K): the reverse fence, in the in-place key's slot.</summary>
    public RedisKey ReverseFence(string inPlaceKey) => InSlotOf(inPlaceKey, "l:" + ShortHash(inPlaceKey));

    /// <summary>The staging key of a forward hash promotion, in the native key's slot.</summary>
    public RedisKey ForwardStaging(string nativeKey, string nonce) => SameSlot(nativeKey, nativeKey + ":stg:" + nonce);

    /// <summary>The staging key of a reverse hash promotion, in the in-place key's slot.</summary>
    public RedisKey ReverseStaging(string inPlaceKey, string nonce) => InSlotOf(inPlaceKey, "s:" + nonce);

    /// <summary>The state hash of the group, tagged by the group name so it sits in one slot.</summary>
    public RedisKey State(string group)
    {
        ValidateName(group, nameof(group));
        return $"{LibraryPrefix}{Keyspace}:{{{group}}}:state";
    }

    /// <summary>The leader lease of the group, in the slot of its state hash.</summary>
    public RedisKey Lead(string group)
    {
        ValidateName(group, nameof(group));
        return $"{LibraryPrefix}{Keyspace}:{{{group}}}:lead";
    }

    /// <summary>The lock of one Clear at a time for a cache, in the slot of the group's state hash.</summary>
    public RedisKey ClearLock(string group, string cacheId)
    {
        ValidateName(group, nameof(group));
        ValidateName(cacheId, nameof(cacheId));
        return $"{LibraryPrefix}{Keyspace}:{{{group}}}:clrlock:{cacheId}";
    }

    /// <summary>The control record of <paramref name="group"/> in the slot of <paramref name="dataKey"/>.</summary>
    public RedisKey ControlFor(string group, string dataKey) => Control(group, SlotOf(dataKey));

    /// <summary>The control record of <paramref name="group"/> in a slot of the slot set.</summary>
    public RedisKey Control(string group, int slot)
    {
        ValidateName(group, nameof(group));
        return Cluster ? $"{LibraryPrefix}{Keyspace}:{{{SlotTags.For(_prefixSlot ?? slot)}}}:ctl:{group}" : $"{LibraryPrefix}{Keyspace}:ctl:{group}";
    }

    /// <summary>The slots that hold the group's control records: one under a tagged prefix or without a cluster, otherwise every slot.</summary>
    public IReadOnlyList<int> SlotSet()
    {
        if (!Cluster)
        {
            return [0];
        }

        if (_prefixSlot is { } only)
        {
            return [only];
        }

        var all = new int[HashSlot.Count];
        for (var i = 0; i < all.Length; i++)
        {
            all[i] = i;
        }

        return all;
    }

    /// <summary>The control records of the group, one per slot of <see cref="SlotSet"/>.</summary>
    public IReadOnlyList<(int Slot, RedisKey Key)> ControlRecords(string group) =>
        [.. SlotSet().Select(slot => (slot, Control(group, slot)))];

    /// <summary>The app's claim key: the library prefix, <c>mx</c> and a tag of the lowercased app name; with the prefix <c>{app}:</c> it is <c>{app}:mx:{{app}}</c>.</summary>
    public string Claim(string app)
    {
        var name = app.ToLowerInvariant();
        if (!ClaimGrammar.ValidGroup(name))
        {
            throw new ArgumentException($"The app name '{app}' cannot hold a claim: it must be 1 to 200 of letters, digits and . _ : > @ + -.", nameof(app));
        }

        return $"{LibraryPrefix}mx:{{{name}}}";
    }

    // True for a prefix that tags every key; false for one with no braces; throws for one whose braces would take part of the key into the tag.
    private static bool PrefixTag(string prefix)
    {
        var open = prefix.IndexOf('{');
        if (open < 0)
        {
            return false;
        }

        var close = prefix.IndexOf('}', open + 1);
        if (close > open + 1)
        {
            return true;
        }

        throw new ArgumentException($"The connection prefix '{prefix}' has an empty or unmatched brace, so the slot of a key would depend on the key's own text.", nameof(prefix));
    }

    private static string ShortHash(string key)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), digest);
        return Convert.ToBase64String(digest[..8]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private RedisKey InSlotOf(string dataKey, string rest)
    {
        if (!Cluster)
        {
            return $"{LibraryPrefix}{Keyspace}:{rest}";
        }

        return $"{LibraryPrefix}{Keyspace}:{{{SlotTags.For(SlotOf(dataKey))}}}:{rest}";
    }

    private string SameSlot(string dataKey, string name)
    {
        if (Cluster && SlotOf(name) != SlotOf(dataKey))
        {
            throw new ArgumentException($"The key '{dataKey}' carries no hash tag, so a name built on it would leave its slot.", nameof(dataKey));
        }

        return name;
    }
}
