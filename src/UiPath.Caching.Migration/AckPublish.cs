namespace UiPath.Caching.Migration;

/// <param name="Applied">False when the store already held a later acknowledgement of this node, which this call did not replace.</param>
/// <param name="State">The switch state, read in the same step.</param>
internal readonly record struct AckPublish(bool Applied, SwitchRead State);
