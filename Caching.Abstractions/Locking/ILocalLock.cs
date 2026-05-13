namespace UiPath.Platform.Caching.Locking;

public interface ILocalLock
{
    ValueTask<IDisposable> AcquireAsync(string key, CancellationToken token);
}
