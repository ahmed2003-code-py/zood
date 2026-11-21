using Zawed.Api.Platform.Crypto;

namespace Zawed.Tests.Support;

/// <summary>
/// Rows a test needs before it can call the API (Java <c>IdentityTestSupport</c> and the module test
/// supports): companies, operators and users, written as the owner. Module agents add their own helpers
/// here (wallets, vehicles, devices), each one inserting what the Java support class inserts.
/// </summary>
public static class Seed
{
    public const string Password = "Correct-Horse-Battery-9";

    private static readonly Lazy<string> PasswordHash = new(() => new PasswordHasher().Hash(Password));

    public static async Task<Guid> CompanyAsync(Owner owner, string name = "Nile Transport")
    {
        var id = Guid.CreateVersion7();
        await owner.ExecuteAsync("""
            insert into companies (id, legal_name, name_ar, name_en, status, cooldown_minutes, low_balance_threshold_piastres, created_at, updated_at)
            values (@id, @name, @name, @name, 'ACTIVE', 120, 0, now(), now())
            """, new { id, name = $"{name} {id:N}" });
        return id;
    }

    public static async Task<Guid> OperatorAsync(Owner owner, string name = "Elite Stations")
    {
        var id = Guid.CreateVersion7();
        await owner.ExecuteAsync("""
            insert into station_operators (id, legal_name, name_ar, name_en, status, created_at, updated_at)
            values (@id, @name, @name, @name, 'ACTIVE', now(), now())
            """, new { id, name = $"{name} {id:N}" });
        return id;
    }

    /// <summary>A user with <see cref="Password"/>; scope id null for a platform role.</summary>
    public static async Task<(Guid Id, string Email)> UserAsync(Owner owner, string role, string scopeType, Guid? scopeId,
        bool mustChangePassword = false, string status = "ACTIVE")
    {
        var id = Guid.CreateVersion7();
        var email = $"{role.ToLowerInvariant()}-{id:N}@zawed.test";
        await owner.ExecuteAsync("""
            insert into users (id, email, full_name, password_hash, role, scope_type, scope_id, status, must_change_password, created_at, updated_at)
            values (@id, @email, 'Test User', @hash, @role, @scopeType, @scopeId, @status, @mustChangePassword, now(), now())
            """, new { id, email, hash = PasswordHash.Value, role, scopeType, scopeId, status, mustChangePassword });
        return (id, email);
    }
}
