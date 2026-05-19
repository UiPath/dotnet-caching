using System.Diagnostics.CodeAnalysis;

namespace UiPath.Platform.Caching;

[SuppressMessage("Major Code Smell", "S3871:Exception types should be \"public\"",
    Justification = "Internal control-flow signal thrown by the rehydrate lambda and caught by RehydrationCoordinator's exception handler to drive cache.rehydrate.failed telemetry. Never escapes the assembly; making it public would expose an implementation detail with no caller value.")]
internal sealed class RehydrateWriteFailedException(string cacheKey)
    : Exception($"Inner cache write failed during rehydrate for key '{cacheKey}'.")
{
}
