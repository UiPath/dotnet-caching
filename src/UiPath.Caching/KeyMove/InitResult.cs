namespace UiPath.Caching.KeyMove;

/// <param name="Code"><see cref="MoveScripts.StateCreated"/>, <see cref="MoveScripts.StateRefreshed"/> or <see cref="MoveScripts.StateNotInitial"/>.</param>
internal sealed record InitResult(long Code, long Ep, string Id);
