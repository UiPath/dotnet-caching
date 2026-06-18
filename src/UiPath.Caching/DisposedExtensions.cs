using System.Diagnostics;

namespace UiPath.Caching;
internal static class DisposedExtensions
{
    [StackTraceHidden, DebuggerStepThrough]
    public static void ThrowIfDisposed(this IDisposable disposable, [DoesNotReturnIf(true)] bool condition)
    {
#if NET7_0_OR_GREATER
        ObjectDisposedException.ThrowIf(condition, disposable);
#else
        if (condition)
        {
            throw new ObjectDisposedException(disposable.GetType().Name);
        }
#endif
    }
}
