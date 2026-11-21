namespace Zawed.Api.Platform.Web;

/// <summary>
/// The one list envelope (Java <c>Page</c>): items plus a cursor for the next page. Every list endpoint is
/// paginated. Ids are UUIDv7, so ordering by id is ordering by time; the cursor is the last id.
/// </summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;

    /// <summary>The limit a request may ask for, clamped to 1–100.</summary>
    public static int Limit(int? requested) => requested is null ? DefaultLimit : Math.Clamp(requested.Value, 1, MaxLimit);

    /// <summary>Builds a page from rows fetched with <c>limit + 1</c>: the extra row only says there is more.</summary>
    public static Page<T> Of(IReadOnlyList<T> rows, int limit, Func<T, Guid> idOf) =>
        rows.Count <= limit ? new Page<T>(rows.ToList(), null) : new Page<T>(rows.Take(limit).ToList(), idOf(rows[limit - 1]).ToString());
}

public static class Cursors
{
    /// <summary>A cursor the client sent back; anything that is not an id is <c>400 INVALID_CURSOR</c>, never a 500.</summary>
    public static Guid? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Guid.TryParse(value, out var id) ? id : throw new ApiException(400, "INVALID_CURSOR", "Invalid cursor", "cursor");
    }
}
