namespace UiPath.Caching.Migration;

/// <summary>Reads the node hash for the pin; an entry naming no application counts as ours, so it can only hold a gate, never open one.</summary>
internal static class PinGateEvaluator
{
    public const string UnattributedNode = "unattributed_node";
    public const string OwnEntryMissing = "own_entry_missing";

    public static PinVerdict Evaluate(IReadOnlyList<NodeEntry> nodes, string? scope, string ownField, IReadOnlyCollection<string> groups, TimeSpan fresh)
    {
        var routers = FreshRouters(nodes, fresh);
        var ours = routers.Where(pair => InScope(pair.Value, scope)).ToList();
        if (!ours.Any(pair => pair.Key == ownField))
        {
            return new PinVerdict(new HashSet<string>(), OwnEntryMissing, []);
        }

        // What names no application may be ours or anyone's: it holds the gate, and its pin, if it has one, approves nothing.
        if (ours.Any(pair => pair.Value.Scope is null))
        {
            return new PinVerdict(new HashSet<string>(), UnattributedNode, []);
        }

        foreach (var node in nodes)
        {
            // A library advertisement is judged through the router entry of its node, whoever it belongs to; with none, it may be ours and has no pin.
            if (node.Age <= fresh
                && !node.Field.EndsWith(RouterAdvertisement.FieldSuffix, StringComparison.Ordinal)
                && IsLiveLibraryAdvertisement(node.Text)
                && !routers.ContainsKey(node.Field + RouterAdvertisement.FieldSuffix))
            {
                return new PinVerdict(new HashSet<string>(), UnattributedNode, []);
            }
        }

        var satisfied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups.Where(group => ours.All(pair => pair.Value.Has(RouterAdvertisement.Pin + group))))
        {
            satisfied.Add(group);
        }

        return new PinVerdict(satisfied, null, [.. ours.Select(pair => pair.Key)]);
    }

    /// <summary>A fresh router entry of this application that reports the group switched.</summary>
    public static bool SwitchedBy(IReadOnlyList<NodeEntry> nodes, string? scope, string group, TimeSpan fresh) =>
        FreshRouters(nodes, fresh).Values.Any(entry => InScope(entry, scope) && entry.Has(RouterAdvertisement.SwitchedPrefix + group));

    private static Dictionary<string, RouterAdvertisement> FreshRouters(IReadOnlyList<NodeEntry> nodes, TimeSpan fresh)
    {
        var routers = new Dictionary<string, RouterAdvertisement>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node.Age <= fresh && node.Field.EndsWith(RouterAdvertisement.FieldSuffix, StringComparison.Ordinal) && RouterAdvertisement.TryParse(node.Text, out var advertisement) && !advertisement.Left)
            {
                routers[node.Field] = advertisement;
            }
        }

        return routers;
    }

    private static bool InScope(RouterAdvertisement advertisement, string? scope) =>
        advertisement.Scope is not { } other || scope is null || other == scope;

    private static bool IsLiveLibraryAdvertisement(string text)
    {
        var hasReads = false;
        foreach (var field in text.Split(';'))
        {
            if (field.StartsWith("vr=", StringComparison.Ordinal))
            {
                hasReads = true;
            }
            else if (field == "left=1")
            {
                return false;
            }
        }

        return hasReads;
    }
}
