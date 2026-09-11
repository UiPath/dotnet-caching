namespace UiPath.Caching.Logging;

/// <summary>What the library is about to name in a log line, so a policy can judge whether it is a secret.</summary>
/// <param name="ValueType">What the cache holds, when the call site knows it: <c>ICache&lt;SessionToken&gt;</c> is a secret, <c>ICache&lt;int&gt;</c> is not.</param>
public readonly record struct MaskingContext(string Key, Type? ValueType, string CacheName);
