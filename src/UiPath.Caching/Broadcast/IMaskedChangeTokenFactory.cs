namespace UiPath.Caching.Broadcast;

/// <summary>Lets a tier that was built with its own masking policy pass that decision into the change tokens it creates.</summary>
internal interface IMaskedChangeTokenFactory
{
    ICacheChangeToken Create(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryType, KeyMasker masker, CacheKey callerKey);
}
