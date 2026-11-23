namespace UiPath.Caching.Tests.KeyMove.Harness;

// Each switch weakens the model in one way a negative control names; None is the specified protocol.
internal sealed record ModelFaults
{
    public static ModelFaults None { get; } = new();

    public bool LengthOnlyToken { get; init; }

    public bool PostPublishRecheck { get; init; }

    public bool AbsentTokenPasses { get; init; }

    public bool CounterTokens { get; init; }

    public bool SourceScriptsSkipIdentity { get; init; }

    public bool ReverseNx { get; init; }

    public bool SourceReadBeforeCapture { get; init; }

    public bool DstFirstReverseWrite { get; init; }

    public bool CtlSyncLowers { get; init; }

    public bool CtlDeletedAtTransition { get; init; }

    public bool AbsentRecordIsOpen { get; init; }

    public bool CacheAdmittedInS3 { get; init; }

    public bool HolderRule { get; init; }

    public bool CaptureBoundFromReply { get; init; }

    public bool S5SkipsSourceDelete { get; init; }

    public bool LsweptWithoutConfirm { get; init; }

    public bool LsweptWithoutWait { get; init; }

    // This fault is outside the negative-control suite. It models an S1 read of a no-origin cache that returns an L miss without confirming the control identity.
    public bool S1MissWithoutIdentityCheck { get; init; }

    // This harness-only fault makes a forward no-origin write delete the source first, showing that the harness can detect a stored key being read as a miss.
    public bool ForwardNoOriginSourceFirst { get; init; }
}
