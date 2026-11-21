using System.Text.Json;
using Npgsql;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Notifications;

/// <summary>
/// Reads a signed-in user's notifications through the API, the way the web bell will (Java <c>support.Notified</c>). A
/// platform user shares the platform's notifications with every other test, so assertions about platform roles look at
/// the notifications about one entity; a fresh company or operator starts with none, so its count can be asserted
/// exactly.
/// </summary>
public static class Notified
{
    /// <summary>The user's newest notifications (up to 100), optionally only the unread ones.</summary>
    public static async Task<List<JsonElement>> ListAsync(IdentityTestSupport test, SignedIn user, bool unreadOnly)
    {
        var response = await test.AuthedAsync("GET", "/api/v1/notifications?limit=100" + (unreadOnly ? "&unread=true" : ""), user.AccessToken);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == 200, body);
        return [.. JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()];
    }

    /// <summary>
    /// Every notification of the user (pages of 100, at most 20 pages). The about-one-entity helpers read this rather
    /// than Java's first 100 only: the test classes run in parallel here and share the platform's feed.
    /// </summary>
    private static async Task<List<JsonElement>> AllAsync(IdentityTestSupport test, SignedIn user)
    {
        var all = new List<JsonElement>();
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var response = await test.AuthedAsync("GET", "/api/v1/notifications?limit=100" + (cursor is null ? "" : "&cursor=" + cursor),
                user.AccessToken);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True((int)response.StatusCode == 200, body);
            var root = JsonDocument.Parse(body).RootElement;
            all.AddRange(root.GetProperty("items").EnumerateArray());
            cursor = root.GetProperty("nextCursor").GetString();
            if (cursor is null)
            {
                break;
            }
        }
        return all;
    }

    /// <summary>The types of the user's notifications that point at this entity, newest first.</summary>
    public static async Task<List<string>> TypesAboutAsync(IdentityTestSupport test, SignedIn user, object entityId) =>
        (await AllAsync(test, user)).Where(n => n.GetProperty("entityId").GetString() == entityId.ToString())
            .Select(n => n.GetProperty("type").GetString()!).ToList();

    /// <summary>The one notification of this type about this entity that the user sees.</summary>
    public static async Task<JsonElement> AboutAsync(IdentityTestSupport test, SignedIn user, object entityId, string type)
    {
        var found = (await AllAsync(test, user))
            .Where(n => n.GetProperty("entityId").GetString() == entityId.ToString() && n.GetProperty("type").GetString() == type).ToList();
        Assert.True(found.Count == 1, $"{type} about {entityId}: {found.Count}");
        return found[0];
    }

    /// <summary>The unread count as the API reports it.</summary>
    public static async Task<int> UnreadAsync(IdentityTestSupport test, SignedIn user)
    {
        var response = await test.AuthedAsync("GET", "/api/v1/notifications/unread-count", user.AccessToken);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == 200, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("count").GetInt32();
    }

    /// <summary>The parameters of a notification as the API returned them.</summary>
    public static Dictionary<string, JsonElement> Params(JsonElement notification) =>
        notification.GetProperty("params").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

    /// <summary>
    /// A transaction as the application's runtime role (<c>zawed_app</c>), told a scope and a user as <c>DbTx</c> would
    /// (the Java tests' <c>scoped(scope, user)</c> on the application's pool). Rolled back at the end; one refused
    /// statement aborts it, so use one per attempt.
    /// </summary>
    public static Task<T> AsAppAsync<T>(Owner owner, Scope? scope, Guid? userId, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work) =>
        owner.AsRuntimeRoleAsync(scope?.Type.ToString(), scope?.Id, userId, work);

    /// <summary>The PostgreSQL refusal of one statement run by <see cref="AsAppAsync{T}"/>.</summary>
    public static Task<PostgresException> RefusedAsAppAsync(Owner owner, Scope? scope, Guid? userId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> work) =>
        AsAppAsync(owner, scope, userId, async (c, tx) => await Assert.ThrowsAsync<PostgresException>(() => work(c, tx)));

    public static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return await command.ExecuteNonQueryAsync();
    }
}
