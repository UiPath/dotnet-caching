using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed record StateRecord(
    string State,
    string Direction,
    long Ep,
    string Id,
    bool Durable,
    int NPop,
    string Prev,
    long? MoveId,
    string Caches,
    string CacheSet,
    string AllAck,
    string Swept,
    string Claim,
    string Release,
    string Move = "",
    string HoldOp = "",
    string HoldAuto = "",
    IReadOnlyDictionary<string, long>? Residue = null,
    IReadOnlyList<string>? FieldNames = null,
    IReadOnlyDictionary<string, string>? Values = null)
{
    private const char CacheSeparator = '+';

    public string Identity => $"{Ep}:{Id}";

    /// <summary>The S3 acknowledgement time in Unix milliseconds, when it belongs to this state ID.</summary>
    public long? S3AckedAt =>
        AllAck == Id && long.TryParse(Field(StateFields.T3At), NumberStyles.None, CultureInfo.InvariantCulture, out var at) ? at : null;

    /// <summary>The forward generation the markers of this run are bound to; empty before the start and after the way back began.</summary>
    public string ForwardGeneration => Field(StateFields.Fg);

    /// <summary>The generation that is retiring in S1(rev); empty elsewhere.</summary>
    public string RetiringGeneration => Field(StateFields.Rg);

    public IReadOnlyList<string> CacheIds => Caches.Length == 0 ? [] : Caches.Split(CacheSeparator);

    /// <summary>The managed cache ids as the hash stores them: sorted, joined with a character no cache name may hold.</summary>
    public static string EncodeCaches(IEnumerable<string> cacheIds)
    {
        var sorted = cacheIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var cid in sorted)
        {
            MoveKeyNames.ValidateName(cid, nameof(cacheIds));
        }

        return string.Join(CacheSeparator, sorted);
    }

    public static string HashOfCaches(string encodedCaches) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encodedCaches))[..8]).ToLowerInvariant();

    /// <summary>The previous-state text <c>st:dir:nPop:ep:id</c> that a transition and DURABLE keep.</summary>
    public static string FormatPrev(string state, string direction, int nPop, long ep, string id) =>
        string.Create(CultureInfo.InvariantCulture, $"{state}:{direction}:{nPop}:{ep}:{id}");

    /// <summary>Null for no hash. A hash that holds no readable identity throws: an unreadable state is never taken for an absent one.</summary>
    public static StateRecord? Parse(HashEntry[] entries)
    {
        if (entries.Length == 0)
        {
            return null;
        }

        var fields = entries.ToDictionary(e => e.Name.ToString(), e => (string?)e.Value, StringComparer.Ordinal);
        string Text(string name) => fields.TryGetValue(name, out var v) && v is not null ? v : string.Empty;
        var ep = Text(StateFields.Ep);
        var id = Text(StateFields.Id);
        var state = Text(StateFields.State);
        if (!long.TryParse(ep, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) || id.Length == 0 || state.Length == 0)
        {
            throw new FormatException("The state hash holds no readable identity.");
        }

        long? moveId = long.TryParse(Text(StateFields.MoveId), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : null;
        _ = int.TryParse(Text(StateFields.NPop), NumberStyles.None, CultureInfo.InvariantCulture, out var nPop);
        return new StateRecord(
            state,
            Text(StateFields.Direction),
            epoch,
            id,
            Text(StateFields.Durable) == "1",
            nPop,
            Text(StateFields.Prev),
            moveId,
            Text(StateFields.Caches),
            Text(StateFields.CacheSet),
            Text(StateFields.AllAck),
            Text(StateFields.Swept),
            Text(StateFields.Claim),
            Text(StateFields.Release),
            Text(StateFields.Move),
            Text(StateFields.HoldOp),
            Text(StateFields.HoldAuto),
            ReadResidue(fields),
            [.. fields.Keys],
            fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value!, StringComparer.Ordinal));
    }

    /// <summary>A field of the hash by name, empty when it is absent.</summary>
    public string Field(string name) => Values is not null && Values.TryGetValue(name, out var value) ? value : string.Empty;

    /// <summary>A cache whose residue count is above zero or unknown keeps the source fallback read; an absent count is unknown.</summary>
    public bool KeepsSourceFallback(string cacheId) => Residue is null || !Residue.TryGetValue(cacheId, out var count) || count > 0;

    /// <summary>The control record this state asks every slot to hold, with a fresh open gate per managed cache.</summary>
    public ControlRecord ToControlRecord(IReadOnlyList<(string Cid, string Nonce)> gates)
    {
        var prev = Prev.Split(':');
        if (prev.Length != 5)
        {
            throw new FormatException($"The state hash holds no previous state to copy into the control records: '{Prev}'.");
        }

        return new ControlRecord(Ep, Id, State, Direction, NPop.ToString(CultureInfo.InvariantCulture), prev[0], prev[1], prev[2], prev[3], prev[4], gates)
        {
            BindingField = ForwardGeneration.Length > 0 || RetiringGeneration.Length > 0 ? StateFields.Fg : string.Empty,
            ForwardGeneration = ForwardGeneration,
            RetiringGeneration = RetiringGeneration,
        };
    }

    private static Dictionary<string, long> ReadResidue(Dictionary<string, string?> fields) =>
        fields.Where(f => f.Key.StartsWith(StateFields.ResiduePrefix, StringComparison.Ordinal) && long.TryParse(f.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .ToDictionary(f => f.Key[StateFields.ResiduePrefix.Length..], f => long.Parse(f.Value!, CultureInfo.InvariantCulture), StringComparer.Ordinal);
}
