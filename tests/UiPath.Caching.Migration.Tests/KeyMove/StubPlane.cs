using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration.Tests.KeyMove;

internal sealed class StubPlane(string registeredGroup, EpochGate gate) : IKeyMovePlane
{
    public GroupRules Rules { get; set; } = GroupRules.Unaffected;

    public bool Residue { get; set; } = true;

    public bool RefuseEntry { get; set; }

    public int DataReads { get; private set; }

    public KeyMoveData? Held { get; set; }

    public KeyMoveData? Data
    {
        get
        {
            DataReads++;
            return Held;
        }

        set => Held = value;
    }

    public List<KeyMoveAlert> Alerts { get; } = [];

    public GroupRules RulesFor(string group) => Rules;

    public bool KeepsSourceFallback(string group, string cacheId) => Residue;

    public AdmittedUnit? Enter(string group, bool promotion) => RefuseEntry ? null : gate.Enter(registeredGroup, promotion);

    public void Alert(string group, string code, string detail) => Alerts.Add(new KeyMoveAlert(group, code, detail));
}
