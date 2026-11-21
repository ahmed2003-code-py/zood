namespace Zawed.Domain.Ids;

/// <summary>
/// UUID version 7 (RFC 9562): a 48-bit Unix millisecond timestamp followed by random bits (ADR 0005).
/// Ids created later sort later, which keeps primary-key indexes compact, and a POS device can create
/// them offline. Two ids created in the same millisecond are ordered by their random bits, so nothing
/// may rely on id order as the order of events. Same layout as the Java <c>platform.ids.UuidV7</c>.
/// </summary>
public static class UuidV7
{
    /// <summary>A version 7 id for the given instant (the BCL implements RFC 9562 exactly).</summary>
    public static Guid Create(DateTimeOffset at) => Guid.CreateVersion7(at);

    /// <summary>The Unix millisecond timestamp carried by a version 7 id.</summary>
    public static long TimestampOf(Guid id)
    {
        if (id.Version != 7)
        {
            throw new ArgumentException($"not a version 7 UUID: {id}", nameof(id));
        }
        // The string form is big-endian whatever the in-memory layout: the first 12 hex digits are the time.
        var hex = id.ToString("N")[..12];
        return Convert.ToInt64(hex, 16);
    }
}
