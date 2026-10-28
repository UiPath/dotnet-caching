namespace UiPath.Caching;

internal delegate TResult KeyCall<in TOwner, out TResult>(TOwner owner, Span<char> key, CancellationToken token);
