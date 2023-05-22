namespace UiPath.Platform.Caching.Broadcast;

[ExcludeFromCodeCoverage]
public sealed class NullChangeTokenFactory : IChangeTokenFactory
{
    public static readonly IChangeTokenFactory Instance = new NullChangeTokenFactory();

    public IChangeToken? Create(Channel channel, string token) => null;
}
