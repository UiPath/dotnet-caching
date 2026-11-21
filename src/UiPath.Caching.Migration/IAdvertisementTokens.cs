namespace UiPath.Caching.Migration;

/// <summary>
/// One source of <c>mig</c> tokens for the router's node entry, asked on every write. A token holds no <c>+</c> or <c>;</c>; group names
/// may not contain <c>+</c>, <c>@</c>, <c>=</c>, <c>{</c> or <c>}</c>. The pin (<c>pin=&lt;g&gt;</c>) and the Phase B tokens come as further sources.
/// </summary>
internal interface IAdvertisementTokens
{
    void AddTo(List<string> tokens);
}
