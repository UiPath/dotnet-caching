using System.ComponentModel;

namespace UiPath.Caching.Telemetry;

/// <summary>Builds <see cref="TelemetryTags{TValue}"/> from a collection expression.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class TelemetryTagsBuilder
{
    public static TelemetryTags<TValue> Create<TValue>(ReadOnlySpan<KeyValuePair<string, TValue>> tags) => new(tags);
}
