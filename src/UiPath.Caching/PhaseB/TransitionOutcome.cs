namespace UiPath.Caching.PhaseB;

internal enum TransitionOutcome
{
    /// <summary>The state shows <c>dur=1</c> for the target identity.</summary>
    Completed,

    /// <summary>The stored state is no longer the one the transition was built on, or another identity replaced it: nothing more to do for this one.</summary>
    StateChanged,

    /// <summary>A control record holds the same <c>ep</c> under another id, or a higher <c>ep</c>: raised as <c>identity_conflict</c>.</summary>
    IdentityConflict,

    /// <summary>A barrier did not confirm before step 4: the transition waits at <c>dur=0</c> and the leader retries.</summary>
    BarrierStuck,

    /// <summary>A CTLCOMMIT was refused or a slot could not be committed after the retries; the transition stays committed-in-part and is resumed.</summary>
    CommitIncomplete,

    /// <summary>The abandon was asked for a transition that is past step 4, that a node acknowledged, or that does not exist.</summary>
    AbandonRefused,

    /// <summary>The controller no longer holds its lease; nothing further was written.</summary>
    LeaseLost,
}
