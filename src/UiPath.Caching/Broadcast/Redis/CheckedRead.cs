namespace UiPath.Caching.Broadcast.Redis;

/// <summary>
/// A check and the read sent behind it on the one connection, so the state checked is the one the read starts from. The
/// parts complete apart; <see cref="Group"/> is null when the check did not ask for the group or it is not there.
/// </summary>
internal readonly record struct CheckedRead(Task<StreamState> Stream, Task<GroupPosition?> Group, Task<StreamEntry[]> Entries);
