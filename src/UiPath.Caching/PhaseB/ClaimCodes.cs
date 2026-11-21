namespace UiPath.Caching.PhaseB;

/// <summary>The scripts' return codes.</summary>
internal static class ClaimCodes
{
    public const long Done = 1;
    public const long AlreadyDone = 2;
    public const long Refused = 0;
    public const long OtherGroup = -3;
    public const long OtherMove = -4;
    public const long PointerChanged = -5;
    public const long TokenSpent = -6;
    public const long FallbackExhausted = -7;
    public const long BadArgument = -8;
    public const long OwnMalformed = -9;
}
