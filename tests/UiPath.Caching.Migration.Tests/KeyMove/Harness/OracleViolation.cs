namespace UiPath.Caching.Migration.Tests.KeyMove.Harness;

internal sealed record OracleViolation(string Kind, string Message, bool Final = false);
