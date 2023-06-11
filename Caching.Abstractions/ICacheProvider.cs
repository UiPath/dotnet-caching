namespace UiPath.Platform.Caching;

public interface ICacheProvider : IDisposable
{
    string Name { get; }

    bool Enabled { get; }

    ICache CreateCache();

    IHashCache CreateHashCache();
}
