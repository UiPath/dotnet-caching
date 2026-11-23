namespace UiPath.Caching.KeyMove;

internal sealed record AdmissionBaseline(ServerGeneration Generation, string Token)
{
    private const char Separator = '|';

    public string Text => string.Join(Separator, Generation.RunId, Generation.ReplicationId, Token);

    public static AdmissionBaseline Parse(string text)
    {
        var parts = text.Split(Separator);
        return parts.Length == 3 && parts[0].Length > 0 && parts[1].Length > 0
            ? new AdmissionBaseline(new ServerGeneration(parts[0], parts[1]), parts[2])
            : throw new FormatException("The generation baseline is not run|replid|token.");
    }
}
