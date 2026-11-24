using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UiPath.Caching.KeyMove;

/// <summary>One glob and the Redis value kind to scan for.</summary>
internal readonly record struct MoveScan(string Pattern, KeyMoveKind Kind)
{
    public string RedisType => Kind == KeyMoveKind.Hash ? "hash" : "string";

    public static IReadOnlyList<MoveScan> For(IGroupKeySpace space, string group, MoveSide side = MoveSide.Legacy)
    {
        var scans = new List<MoveScan>();
        foreach (var pattern in space.Patterns(group, side))
        {
            if (space.HoldsStrings(group, side, pattern))
            {
                scans.Add(new MoveScan(pattern, KeyMoveKind.String));
            }

            if (space.HoldsHashes(group, side, pattern))
            {
                scans.Add(new MoveScan(pattern, KeyMoveKind.Hash));
            }
        }

        return scans;
    }

    public static string HashOf(IReadOnlyList<MoveScan> scans)
    {
        var text = new StringBuilder();
        foreach (var scan in scans)
        {
            text.Append(scan.RedisType).Append(scan.Pattern.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(scan.Pattern);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }
}
