namespace Zawed.Api.Platform.Security;

/// <summary>
/// What a role may do: the single source of truth for authorisation, a port of the Java
/// <c>Permission</c> enum and its role table. Endpoints declare the permission they need with
/// <see cref="RequiresPermissionAttribute"/>; nothing else grants access. The authorisation matrix test
/// checks every endpoint against every role.
/// </summary>
public enum Permission
{
    // B1 identity
    USERS_READ,
    USERS_CREATE,
    USERS_STATUS,
    USERS_RESET_PASSWORD,
    USERS_UNLOCK,
    USERS_MFA_RESET,
    AUDIT_READ,

    // B2 master data
    OPS_READ,
    COMPANIES_MANAGE,
    COMPANY_POLICY_MANAGE,
    FLEET_MANAGE,
    NETWORK_MANAGE,
    PRICES_MANAGE,
    COMPANY_READ,
    STATION_READ,

    // B3 ledger and wallet
    TOPUP_REQUEST,
    WALLET_CREDIT,
    RECONCILIATION_RUN,
    TOPUP_APPROVE,

    // B4 fueling
    ALERTS_MANAGE,

    // B6 station finance (dual control: the person, not the role, is the control)
    PAYOUTS_MANAGE,
    PAYOUTS_APPROVE,
    BANK_DETAILS_MANAGE,
    BANK_DETAILS_APPROVE,
    REVERSALS_REQUEST,
    REVERSALS_APPROVE,
    PAYABLE_HOLD_RELEASE,

    // B6 tickets
    TICKETS_OPEN,
    TICKETS_MANAGE,

    // U1 leads
    LEADS_MANAGE,
}

public static class Permissions
{
    private static readonly HashSet<Permission> PlatformOperations =
    [
        Permission.USERS_READ, Permission.USERS_RESET_PASSWORD, Permission.USERS_UNLOCK, Permission.OPS_READ,
        Permission.COMPANIES_MANAGE, Permission.FLEET_MANAGE, Permission.NETWORK_MANAGE, Permission.PRICES_MANAGE,
        Permission.WALLET_CREDIT, Permission.ALERTS_MANAGE, Permission.PAYOUTS_MANAGE, Permission.PAYOUTS_APPROVE,
        Permission.BANK_DETAILS_MANAGE, Permission.BANK_DETAILS_APPROVE, Permission.REVERSALS_REQUEST,
        Permission.REVERSALS_APPROVE, Permission.PAYABLE_HOLD_RELEASE, Permission.TICKETS_MANAGE, Permission.LEADS_MANAGE,
    ];

    private static readonly HashSet<Permission> CompanyUsers =
        [Permission.COMPANY_READ, Permission.TOPUP_REQUEST, Permission.TICKETS_OPEN];

    private static readonly IReadOnlyDictionary<Role, IReadOnlySet<Permission>> ByRole = new Dictionary<Role, IReadOnlySet<Permission>>
    {
        [Role.SUPER_ADMIN] = Enum.GetValues<Permission>()
            .Except([Permission.COMPANY_READ, Permission.STATION_READ, Permission.TOPUP_REQUEST, Permission.TICKETS_OPEN])
            .ToHashSet(),
        [Role.OPS_MANAGER] = PlatformOperations.Append(Permission.TOPUP_APPROVE).ToHashSet(),
        [Role.OPS] = PlatformOperations,
        [Role.FLEET_MANAGER] = CompanyUsers,
        [Role.ACCOUNTANT] = CompanyUsers,
        [Role.STATION_MANAGER] = new HashSet<Permission> { Permission.STATION_READ, Permission.TICKETS_OPEN },
    };

    public static IReadOnlySet<Permission> Of(Role role) => ByRole[role];

    /// <summary>The authority name the Java API used, e.g. <c>PERM_USERS_READ</c> (kept for logs and tests).</summary>
    public static string Authority(this Permission permission) => "PERM_" + permission;
}
