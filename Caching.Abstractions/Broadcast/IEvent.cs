namespace UiPath.Platform.Caching.Broadcast;

public interface IEvent
{
    string? Id { get; }

    string? Type { get; }

    Uri? Source { get; }

    bool IsValid();
}
