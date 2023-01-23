namespace UiPath.Platform.Caching.Polly;

public class ExecutePoliciesOptions
{
    public TimeSpan DurationOfBreak { get; set; } = TimeSpan.FromMinutes(1);

    public int ExceptionsAllowedBeforeBreaking { get; set; } = 10;

    public TimeSpan? RequestTimeout { get; set; }

    public int? RetryCount { get; set; }
}
