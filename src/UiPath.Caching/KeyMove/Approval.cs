using System.Security.Cryptography;
using System.Text;

namespace UiPath.Caching.KeyMove;

/// <summary>A stored consent <c>appr:&lt;kind&gt;</c>: void when the state moves or the evidence it covers changes.</summary>
/// <param name="Evidence">Hash of the inventory it acknowledged, empty for a kind with no evidence.</param>
internal sealed record Approval(string Kind, string Identity, string Evidence, string Operator, string Confirm)
{
    /// <summary>The evidence the stored state offers for <paramref name="kind"/>: the residue counts for a close or a release, nothing otherwise.</summary>
    public static string EvidenceOf(string kind, StateRecord state)
    {
        if (kind is not (ApprovalKinds.Close or ApprovalKinds.CloseForce or ApprovalKinds.Release))
        {
            return string.Empty;
        }

        var residue = string.Join(';', (state.Residue ?? new Dictionary<string, long>()).OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key}={r.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(residue)))[..16].ToLowerInvariant();
    }

    public static Approval? Of(StateRecord state, string kind) => TryParse(kind, state.Field(StateFields.ApprovalPrefix + kind));

    public static Approval? ValidFor(StateRecord state, string kind) =>
        Of(state, kind) is { } approval && approval.Identity == state.Identity && approval.Evidence == EvidenceOf(kind, state) ? approval : null;

    public static Approval? TryParse(string kind, string text)
    {
        var parts = text.Split('|', 4);
        return parts.Length == 4 ? new Approval(kind, parts[0], parts[1], Uri.UnescapeDataString(parts[2]), Uri.UnescapeDataString(parts[3])) : null;
    }

    public string Encode() => $"{Identity}|{Evidence}|{Uri.EscapeDataString(Operator)}|{Uri.EscapeDataString(Confirm)}";
}
