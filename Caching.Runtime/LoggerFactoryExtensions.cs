namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
internal static class LoggerFactoryExtensions
{
    internal static ILogger<T> Create<T>(this ILoggerFactory? loggerFactory) =>
        loggerFactory?.CreateLogger<T>() ?? NullLogger<T>.Instance;
}
