namespace UiPath.Platform.Caching.Broadcast;

public interface IEvent
{
    string? Id { get; }

    string? TransportId { get; }

    string? Type { get; }

    Uri? Source { get; }

    string? Key { get; }

    bool IsValid();

    void AttachTransportId(string? transportId);
}
