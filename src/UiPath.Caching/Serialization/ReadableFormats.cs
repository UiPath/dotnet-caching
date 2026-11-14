namespace UiPath.Caching.Serialization;

/// <summary>The formats one policy's reads accept; <see cref="Any"/> when it lists none.</summary>
internal sealed class ReadableFormats
{
    /// <summary>What <see cref="ReadOutcome{T}.ExcludedFormat"/> says for headerless bytes outside the list.</summary>
    public const int JsonId = -2;

    private readonly int[]? _ids;

    private ReadableFormats(bool json, int[]? ids)
    {
        Json = json;
        _ids = ids;
    }

    public static ReadableFormats Any { get; } = new(true, null);

    public bool Json { get; }

    /// <param name="write">The format the policy writes, always readable; null for JSON.</param>
    /// <param name="jsonFallback">The write may turn into JSON while the node list holds the format back, so JSON stays readable.</param>
    public static ReadableFormats Resolve(ValueSerializerRegistry registry, IReadOnlyList<string> read, ICacheValueSerializer? write, bool jsonFallback)
    {
        var json = write is null || jsonFallback;
        var ids = new List<int>();
        foreach (var name in read)
        {
            if (!registry.TryResolve(name, out var serializer))
            {
                throw new InvalidOperationException(
                    $"The cache policy names the value serializer '{name}' in Serializer.Read, but none with that name is registered. Register it with AddValueSerializer, or use \"json\".");
            }

            if (serializer is null)
            {
                json = true;
            }
            else
            {
                ids.AddRange(registry.FormatIdsOf(serializer).ToArray());
            }
        }

        if (write is not null)
        {
            ids.AddRange(registry.FormatIdsOf(write).ToArray());
        }

        return new(json, [.. ids]);
    }

    public bool Accepts(int formatId) => _ids is null || Array.IndexOf(_ids, formatId) >= 0;
}
