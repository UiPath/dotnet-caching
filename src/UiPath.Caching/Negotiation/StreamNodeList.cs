namespace UiPath.Caching.Negotiation;

/// <summary>Finds the live nodes of the Streams transport that advertise nothing: a group whose consumer read recently and that no advertisement as fresh as that read accounts for, even one of a node that has left.</summary>
internal sealed class StreamNodeList(IStreamGroupSource groups, FormatNegotiationOptions options)
{
    public async Task<IReadOnlyList<string>> BlockersAsync(IEnumerable<RedisKey> streams, IReadOnlyDictionary<string, TimeSpan> advertisementAges, CancellationToken cancellationToken)
    {
        var ages = new Dictionary<string, TimeSpan>(advertisementAges, StringComparer.OrdinalIgnoreCase);
        var blockers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var stream in streams)
        {
            foreach (var group in await groups.GroupsAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                if (blockers.Contains(group))
                {
                    continue;
                }

                // A group no consumer ever read from belongs to no running node, so it counts as nobody.
                var idle = await groups.IdleAsync(stream, group, cancellationToken).ConfigureAwait(false);
                if (idle is not { } sinceRead || sinceRead > options.LivenessWindow)
                {
                    continue;
                }

                // An advertisement older than the group's last read is a previous run's under the same id, which may read less.
                var accountedFor = ages.TryGetValue(group, out var age) && age <= sinceRead + TimeSpan.FromTicks(options.AdvertiseInterval.Ticks * 2);
                if (!accountedFor)
                {
                    blockers.Add(group);
                }
            }
        }

        return [.. blockers];
    }
}
