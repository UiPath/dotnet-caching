namespace UiPath.Caching.KeyMove;

/// <summary>Each advert is aged against its own server’s clock. Acknowledgements need fresh matching adverts on both sides.</summary>
internal sealed class TwoServerAdverts(IAdvertSource src, IAdvertSource dst, TimeSpan fresh) : IAdvertSource
{
    public async Task<IReadOnlyList<NodeAdvert>> ReadAsync(CancellationToken cancellationToken)
    {
        var reads = await Task.WhenAll(dst.ReadAsync(cancellationToken), src.ReadAsync(cancellationToken)).ConfigureAwait(false);
        var byNode = new Dictionary<string, (NodeAdvert? Dst, NodeAdvert? Src)>(StringComparer.Ordinal);
        foreach (var advert in reads[0])
        {
            byNode[advert.Node] = (advert, byNode.TryGetValue(advert.Node, out var held) ? held.Src : null);
        }

        foreach (var advert in reads[1])
        {
            byNode[advert.Node] = (byNode.TryGetValue(advert.Node, out var held) ? held.Dst : null, advert);
        }

        return [.. byNode.Select(static n => n.Value).Select(Merge)];
    }

    private static string GroupOf(string identityToken) => identityToken[..identityToken.IndexOf('@', StringComparison.Ordinal)];

    private static bool IsIdentity(string token) =>
        !token.StartsWith('@') && token.Contains('@') && !token.Contains('=');

    private static List<string> MainTokens(NodeAdvert[] live)
    {
        var bothFresh = live.Length == 2;
        var tokens = new List<string>();
        foreach (var token in live[0].Tokens)
        {
            if (IsIdentity(token))
            {
                tokens.Add(bothFresh && live.All(a => a.Tokens.Contains(token, StringComparer.Ordinal)) ? token : $"{GroupOf(token)}@0");
            }
            else if (token != NodeAdvert.Km || live.All(a => a.Has(NodeAdvert.Km)))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static void AddPeerTokens(NodeAdvert[] live, List<string> tokens)
    {
        // Identities and flags that only the other fresh advert carries.
        foreach (var token in live.Skip(1).SelectMany(static a => a.Tokens).Distinct(StringComparer.Ordinal))
        {
            if (tokens.Contains(token, StringComparer.Ordinal))
            {
                continue;
            }

            if (IsIdentity(token) && !tokens.Any(t => IsIdentity(t) && GroupOf(t) == GroupOf(token)))
            {
                tokens.Add($"{GroupOf(token)}@0");
            }
            else if (token is NodeAdvert.Bypass or NodeAdvert.CtlQuestion)
            {
                tokens.Add(token);
            }
        }
    }

    private NodeAdvert Merge((NodeAdvert? Dst, NodeAdvert? Src) node)
    {
        var all = new[] { node.Dst, node.Src }.OfType<NodeAdvert>().ToArray();
        var live = all.Where(a => a.Age <= fresh).ToArray();
        if (live.Length == 0)
        {
            return all[0];
        }

        var tokens = MainTokens(live);
        AddPeerTokens(live, tokens);
        return new NodeAdvert(live[0].Node, live[0].Age, tokens);
    }
}
