using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>What the router's data path takes from the key move node: the rules of a group, the admission of units, and the data connection.</summary>
internal interface IKeyMovePlane
{
    KeyMoveData? Data { get; }

    GroupRules RulesFor(string group);

    bool KeepsSourceFallback(string group, string cacheId);

    AdmittedUnit? Enter(string group, bool promotion);

    void Alert(string group, string code, string detail);
}
