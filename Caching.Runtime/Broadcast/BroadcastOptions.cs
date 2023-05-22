namespace UiPath.Platform.Caching.Broadcast;

public class BroadcastOptions
{
    internal static readonly Uri MachineUri = new($"urn:{Environment.MachineName}");

    public string ChannelPrefix { get; set; } = "cache";

    public Uri? SourceUri { get; set; } = MachineUri;
}
