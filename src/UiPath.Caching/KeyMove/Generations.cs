namespace UiPath.Caching.KeyMove;

/// <summary>The state-hash fields that carry a transition's change of the forward and retiring generations.</summary>
internal static class Generations
{
    public static (IReadOnlyList<(string Field, string Value)> Set, IReadOnlyList<string> Clear) Of(BindingChange change, StateRecord state, string newId) => change switch
    {
        BindingChange.Mint => ([(StateFields.Fg, newId)], [StateFields.Rg]),
        BindingChange.Retire => (state.ForwardGeneration.Length > 0 ? [(StateFields.Rg, state.ForwardGeneration)] : [], [StateFields.Fg]),
        BindingChange.ClearRetiring => ([], [StateFields.Rg]),
        BindingChange.ClearAll => ([], [StateFields.Fg, StateFields.Rg]),
        _ => ([], []),
    };

    /// <summary>The abandon of a transition, toward the last committed state: its generations are the <c>pfg</c> and <c>prg</c> that DURABLE copied.</summary>
    public static (IReadOnlyList<(string Field, string Value)> Set, IReadOnlyList<string> Clear) Restored(StateRecord abandoned)
    {
        var set = new List<(string, string)>();
        var clear = new List<string>();
        foreach (var (field, previous) in new[] { (StateFields.Fg, StateFields.PrevFg), (StateFields.Rg, StateFields.PrevRg) })
        {
            if (abandoned.Field(previous) is { Length: > 0 } value)
            {
                set.Add((field, value));
            }
            else
            {
                clear.Add(field);
            }
        }

        return (set, clear);
    }
}
