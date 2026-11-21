namespace UiPath.Caching.PhaseB;

/// <summary><see cref="Code"/> is the first attempt's code; a repeat never replaces it.</summary>
internal readonly record struct ClaimResult(long Code, ClaimDurability Durability);
