namespace UiPath.Caching.Migration;

/// <summary>What a read-only probe of one key found.</summary>
/// <param name="Class">One of <see cref="DryRunClasses"/>.</param>
/// <param name="SourceBytes">The length of the value on the in-place side; zero when it was not read.</param>
/// <param name="EncodedBytes">The length the value has encoded for the native side; null unless the key was decoded and is movable.</param>
internal readonly record struct DryRunProbe(string Class, long SourceBytes, long? EncodedBytes);
