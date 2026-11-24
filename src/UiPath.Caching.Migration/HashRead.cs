namespace UiPath.Caching.Migration;

/// <summary>What a path client read of one hash.</summary>
/// <param name="Fields">The user fields of a found hash; a field stored without a value reads as null.</param>
/// <param name="Expiration">When the hash expires, or null for none.</param>
/// <param name="Metadata">The user metadata the hash holds, or null.</param>
/// <param name="Version">The write count the hash holds, or 0.</param>
/// <param name="Uncached">Served but not for the local tier: a promotion that was abandoned.</param>
internal readonly record struct HashRead<T>(ReadOutcome Kind, IDictionary<string, T?>? Fields, DateTimeOffset? Expiration, IDictionary<string, string?>? Metadata = null, long Version = 0, bool Uncached = false)
    where T : class
{
    /// <summary>A miss that is the answer: the hash is cached empty, so a union read stops there and never falls back to an older hash.</summary>
    public bool Stops { get; init; }

    /// <summary>A miss that a union read may still answer from the next side.</summary>
    public bool FallsThrough => Kind == ReadOutcome.Absent && !Stops;

    public static HashRead<T> Of(ReadOutcome kind) => new(kind, null, null);

    /// <summary>The fields asked for that the hash holds, with the rest of the read as it is; a read that found nothing stays as it was.</summary>
    public HashRead<T> Only(IReadOnlyCollection<string> fields)
    {
        if (Kind != ReadOutcome.Found || Fields is null)
        {
            return this;
        }

        var kept = new Dictionary<string, T?>(fields.Count, StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (Fields.TryGetValue(field, out var value))
            {
                kept[field] = value;
            }
        }

        return this with { Fields = kept };
    }
}
