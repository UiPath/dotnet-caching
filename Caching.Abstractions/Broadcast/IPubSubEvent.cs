namespace UiPath.Platform.Caching.Broadcast;

public interface IPubSubEvent
{
    string? Id { get; }

    string? Type { get; }

    Uri? Source { get; }

    bool IsValid();
}
