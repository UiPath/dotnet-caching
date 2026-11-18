namespace UiPath.Caching.Migration;

internal sealed record LocalEntry(object Value, DateTimeOffset Deadline, long Epoch);
