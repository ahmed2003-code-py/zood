namespace Zawed.Api.Platform.Security;

/// <summary>
/// Web portal roles (PRD §3). Declared with their wire and database names (ADR 0029). OPS_MANAGER is
/// Operations plus the approval of money entering wallets (ADR 0021).
/// </summary>
public enum Role
{
    SUPER_ADMIN,
    OPS_MANAGER,
    OPS,
    FLEET_MANAGER,
    ACCOUNTANT,
    STATION_MANAGER,
}

public static class Roles
{
    /// <summary>The only kind of scope a user with this role can have.</summary>
    public static ScopeType ScopeType(this Role role) => role switch
    {
        Role.SUPER_ADMIN or Role.OPS_MANAGER or Role.OPS => Security.ScopeType.PLATFORM,
        Role.FLEET_MANAGER or Role.ACCOUNTANT => Security.ScopeType.COMPANY,
        Role.STATION_MANAGER => Security.ScopeType.OPERATOR,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static bool IsPlatform(this Role role) => role.ScopeType() == Security.ScopeType.PLATFORM;

    /// <summary>Roles that must use a second factor (ADR 0009; the Station Manager since B6).</summary>
    public static bool RequiresMfa(this Role role) =>
        role is Role.SUPER_ADMIN or Role.OPS_MANAGER or Role.OPS or Role.STATION_MANAGER;
}
