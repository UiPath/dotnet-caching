namespace UiPath.Platform.Caching.CloudEvents;

public abstract class CloudEventWrapper : IEvent
{
    public abstract string? Id { get; set; }

    public abstract Uri? Source { get; set; }

    public abstract bool IsValid();


    public abstract CloudEvent CloudEvent { get; }

    public abstract string? Type { get; set; }
}
