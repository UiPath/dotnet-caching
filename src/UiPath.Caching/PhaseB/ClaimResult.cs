namespace UiPath.Caching.PhaseB;

/// <summary>What a durable step returned and whether WAIT confirmed it.</summary>
internal readonly record struct ClaimResult(long Code, ClaimDurability Durability);
