namespace UiPath.Caching.Migration;

/// <summary>One source of <c>mig</c> tokens for the router's entry, asked on every write; a token holds no <c>+</c> or <c>;</c>.</summary>
internal interface IAdvertisementTokens
{
    void AddTo(List<string> tokens);
}
