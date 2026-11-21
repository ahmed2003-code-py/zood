using System.Diagnostics.Metrics;

namespace Zawed.Api.Modules.Notifications;

/// <summary>
/// <c>zawed.notifications.dropped</c>, tagged <c>type</c> (the Java Micrometer counter): notifications refused and rolled
/// back to their savepoint while their cause committed without them. One series per type, observed from the start, so
/// each reads 0 until something is dropped, as Java registered one counter per type at boot. Alert on any value above 0.
/// </summary>
public sealed class NotificationMetrics
{
    public const string MeterName = "Zawed.Notifications";
    public const string DroppedMetric = "zawed.notifications.dropped";

    private readonly long[] dropped = new long[Enum.GetValues<NotificationType>().Length];

    public NotificationMetrics(IMeterFactory meters) =>
        meters.Create(MeterName).CreateObservableCounter(DroppedMetric, () => Enum.GetValues<NotificationType>()
                .Select(type => new Measurement<long>(Dropped(type), new KeyValuePair<string, object?>("type", type.ToString()))),
            description: "Notifications refused and rolled back to their savepoint; the cause committed without them");

    /// <summary>How many notifications of this type were dropped since the start.</summary>
    public long Dropped(NotificationType type) => Interlocked.Read(ref dropped[(int)type]);

    internal void CountDropped(NotificationType type) => Interlocked.Increment(ref dropped[(int)type]);
}
