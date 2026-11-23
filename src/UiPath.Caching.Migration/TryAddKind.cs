namespace UiPath.Caching.Migration;

internal enum TryAddKind
{
    Created,
    Exists,
    Failed,

    /// <summary>The identity changed under the call: it runs again under the new one.</summary>
    Rerun,
}
