namespace UiPath.Caching.PhaseB;

/// <summary>The state identity a node advertises for a group as <c>&lt;g&gt;@&lt;ep&gt;:&lt;id&gt;:&lt;st&gt;&lt;f|r&gt;</c>.</summary>
internal sealed record AdvertIdentity(long Ep, string Id, string State, string Direction)
{
    public bool Matches(StateRecord state) => state.Ep == Ep && state.Id == Id;

    public bool Matches(StateRecord state, string direction) => Matches(state) && state.State == State && Direction == direction;
}
