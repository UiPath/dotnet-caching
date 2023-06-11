namespace UiPath.Platform.Caching;
public sealed class Disposable
{
    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();

        private EmptyDisposable()
        {
        }

        public void Dispose()
        {
            // no op
        }
    }

    public static IDisposable Empty => EmptyDisposable.Instance;
}


