using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed class KeyMoveTokens(KeyMoveAdvertTokens keyMoveTokens) : IAdvertisementTokens
{
    public void AddTo(List<string> tokens) => keyMoveTokens.AddTo(tokens);
}
