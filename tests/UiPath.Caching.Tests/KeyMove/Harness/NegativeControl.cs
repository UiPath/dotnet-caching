namespace UiPath.Caching.Tests.KeyMove.Harness;

/// <param name="Build">The world a control runs: with <see cref="ModelFaults.None"/> it must hold, with <paramref name="Faults"/> it must break.</param>
/// <param name="Kind">The invariant the weakened model must break.</param>
internal sealed record NegativeControl(string Name, ModelFaults Faults, Func<ModelFaults, World> Build, string Kind);
