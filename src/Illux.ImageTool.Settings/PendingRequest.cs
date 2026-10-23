namespace Illux.ImageTool.Settings;

/// <summary>A request the service has not answered yet ("Checking...", "Asking the service..."). It gives up after a while.</summary>
internal sealed class PendingRequest(DateTimeOffset startedAt, TimeSpan? timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public DateTimeOffset StartedAt { get; } = startedAt;

    public bool IsExpired(DateTimeOffset now) => now - StartedAt >= (timeout ?? DefaultTimeout);

    /// <summary>True when the service checked after the request was made (a small allowance covers clock rounding).</summary>
    public bool IsAnsweredByCheckAt(DateTimeOffset? lastCheck) => lastCheck is { } at && at >= StartedAt - TimeSpan.FromSeconds(2);
}
