namespace UiPath.Caching.PhaseB;

/// <param name="Code"><c>replica_unhealthy</c>, <c>replicas_unknown</c>, <c>wait_unavailable</c>, <c>scripts_unsupported</c>, <c>eviction_policy</c> or <c>eviction_policy_unknown</c>.</param>
internal sealed record PreflightFailure(string Code, string Detail);
