namespace UiPath.Caching.Migration.Tests.KeyMove.Harness;

internal sealed record OracleKey(string Name, bool NoOrigin = false, string? InitialL = null, string? InitialN = null);
