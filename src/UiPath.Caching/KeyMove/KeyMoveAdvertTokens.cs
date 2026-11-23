namespace UiPath.Caching.KeyMove;

/// <param name="recycled">The <c>ep</c> of the S0 identity after which the node recycled its data connections, or null.</param>
internal sealed class KeyMoveAdvertTokens(
    IReadOnlyList<GroupRegistration> groups,
    Func<string, IReadOnlyList<string>> caches,
    StageFlagPoller flags,
    IAckedIdentitySource acks,
    EpochGate gate,
    GroupStateMonitor monitor,
    Func<string, long?>? recycled = null)
{
    public void AddTo(List<string> tokens)
    {
        tokens.Add(NodeAdvert.Km);
        foreach (var group in groups)
        {
            var rules = monitor.RulesFor(group.Name);
            if (group.Listed)
            {
                tokens.Add($"kmg={group.Name}");
            }

            if (!group.Listed && !group.Flagged && !rules.Relevant)
            {
                continue;
            }

            tokens.Add($"ca={group.Name}:{string.Join(',', caches(group.Name).Order(StringComparer.Ordinal))}");
            if (flags.ViewOf(group.Name) is { } view)
            {
                tokens.Add(NodeAdvert.Flag(group.Name, view));
            }

            tokens.Add(NodeAdvert.Identity(group.Name, acks.AckedFor(group.Name)));
            tokens.Add($"pi={group.Name}:{gate.PromotionUnits(group.Name)}");
            if (recycled?.Invoke(group.Name) is { } ep)
            {
                tokens.Add($"rc={group.Name}:{ep}");
            }
        }

        if (monitor.SnapshotAge() is { } age)
        {
            tokens.Add(NodeAdvert.Snapshot(age));
        }
    }
}
