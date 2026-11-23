using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal readonly record struct KeyMovePlan(KeyMoveMode Mode, GroupRules Rules)
{
    public ActedState? Act => Rules.Acting;

    public string Identity => Act is { } act ? $"{act.Ep}:{act.Id}:{act.Direction}" : string.Empty;
}
