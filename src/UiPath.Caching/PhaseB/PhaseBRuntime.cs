namespace UiPath.Caching.PhaseB;

/// <summary>The parts of a started Phase B node that operator commands and the router use.</summary>
internal sealed record PhaseBRuntime(
    StateStore Store,
    StageCommands Commands,
    GroupDataOps Data,
    ClearCoordinator Clear,
    GroupRemoval Removal,
    StateRecovery Recovery,
    StageDriver? Driver);
