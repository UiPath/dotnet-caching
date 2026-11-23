using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>A null write means the script would return the code without changing the record.</summary>
internal sealed record ConditionPlan(long Code, IReadOnlyList<Condition> Conditions, Action<ITransaction>? Writes);
