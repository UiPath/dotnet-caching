using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The plane over a key move node: the rules come from its snapshot monitor through a short memo, so a hot path allocates nothing while the rules do not change.</summary>
internal sealed class NodePlane(KeyMoveNode node, TimeProvider clock, Action<KeyMoveAlert>? alert = null) : IKeyMovePlane
{
    private static readonly TimeSpan MemoLife = TimeSpan.FromMilliseconds(100);

    private readonly RulesMemo _memo = new(clock, MemoLife);
    private readonly ConnectionEvents _events = new();
    private readonly Func<string, GroupRules> _read = node.Monitor.RulesFor;
    private KeyMoveData? _data;
    private KeyMoveDataAccess? _access;

    public KeyMoveData? Data
    {
        get
        {
            if (node.DataAccess is not { } access)
            {
                return null;
            }

            var held = _data;
            if (held is not null && ReferenceEquals(_access, access))
            {
                return held;
            }

            _events.Attach(access.Mux);
            var made = new KeyMoveData(access.Db, access.Scripts, access.Names, _events.Count, () => access.Mux.IsConnected);
            _access = access;
            _data = made;
            return made;
        }
    }

    public GroupRules RulesFor(string group) => _memo.Get(group, _read);

    public bool KeepsSourceFallback(string group, string cacheId) => node.Monitor.CurrentState(group)?.KeepsSourceFallback(cacheId) ?? true;

    public AdmittedUnit? Enter(string group, bool promotion) => node.Gate.Enter(group, promotion);

    public void Alert(string group, string code, string detail) => alert?.Invoke(new KeyMoveAlert(group, code, detail));
}
