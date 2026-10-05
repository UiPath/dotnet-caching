namespace UiPath.Caching.Telemetry;

public static class TelemetryTags
{
    public static Dictionary<string, string>? ToDictionaryOrNull(TelemetryTags<string> tags)
    {
        if (tags.IsEmpty)
        {
            return null;
        }
        var dict = new Dictionary<string, string>(tags.Count);
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }
        return dict;
    }

    public static Dictionary<string, double>? ToDictionaryOrNull(TelemetryTags<double> tags)
    {
        if (tags.IsEmpty)
        {
            return null;
        }
        var dict = new Dictionary<string, double>(tags.Count);
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }
        return dict;
    }
}
