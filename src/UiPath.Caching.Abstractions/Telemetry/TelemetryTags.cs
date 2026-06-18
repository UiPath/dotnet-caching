namespace UiPath.Caching.Telemetry;

public static class TelemetryTags
{
    public static Dictionary<string, string>? ToDictionaryOrNull(ReadOnlySpan<KeyValuePair<string, string>> tags)
    {
        if (tags.IsEmpty)
        {
            return null;
        }
        var dict = new Dictionary<string, string>(tags.Length);
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }
        return dict;
    }

    public static Dictionary<string, double>? ToDictionaryOrNull(ReadOnlySpan<KeyValuePair<string, double>> tags)
    {
        if (tags.IsEmpty)
        {
            return null;
        }
        var dict = new Dictionary<string, double>(tags.Length);
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }
        return dict;
    }
}
