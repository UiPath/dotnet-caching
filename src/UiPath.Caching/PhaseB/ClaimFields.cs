namespace UiPath.Caching.PhaseB;

/// <summary>The fields a recreated or reasserted state takes from the move claim record: the descriptor <c>mv</c>, the token <c>ct</c> and the certificate copy <c>rel</c>.</summary>
internal sealed record ClaimFields(string? Move, string? Token, string? Release);
