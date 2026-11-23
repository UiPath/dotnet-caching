namespace UiPath.Caching.KeyMove;

/// <param name="Swept">The L sweep of an S5 identity completed (<c>lswept</c> equals the id).</param>
internal sealed record ActedState(string State, string Direction, long Ep, string Id, int NPop, bool Swept)
{
    public static ActedState From(StateRecord record) => new(record.State, record.Direction, record.Ep, record.Id, record.NPop, record.Swept == record.Id);
}
