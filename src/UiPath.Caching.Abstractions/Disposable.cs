namespace UiPath.Caching;

public sealed class Disposable
{
    public static IDisposable Empty => EmptyDisposable.Instance;

    public static IDisposable Create<TState>(TState state, Action<TState> action) =>
        new ActionDisposable<TState>(state, action);

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

    private sealed class ActionDisposable<TState>(TState state, Action<TState> action) : IDisposable
    {
        private Action<TState>? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke(state);
    }
}
