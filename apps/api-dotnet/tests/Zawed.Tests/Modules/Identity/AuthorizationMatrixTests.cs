using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Zawed.Api.Platform.Security;
using Zawed.Tests.Support;

namespace Zawed.Tests.Modules.Identity;

/// <summary>
/// Every endpoint × every role, against an explicit table written by hand (contract §4; Java
/// <c>AuthorizationMatrixTests</c>). The table is not derived from the attributes, so a wrong attribute fails here
/// instead of being echoed. An endpoint missing from the table fails the first test: nothing ships without a decision.
/// <para>The table is the whole Java one, ported once (wave 1A). A row is checked as soon as the .NET API serves its
/// endpoint; until then it must be a Java operation still listed in <c>pending-operations.txt</c>. So each later
/// module gets its rows checked by merging, and corrects its row here only if its decision changed (it should not).</para>
/// <para>Public so that the IDOR sweep (wave 5A) reads the same table and the same endpoint discovery.</para>
/// </summary>
public sealed partial class AuthorizationMatrixTests(SharedApi api) : IClassFixture<SharedApi>
{
    /// <summary>Who may call an endpoint: anyone (each protecting itself with its own proof), a device only, or roles.</summary>
    public sealed record Access(string Kind, IReadOnlySet<Role> Roles);

    /// <summary>Who may call it, and a body that passes validation, so an allowed caller reaches the service.</summary>
    public sealed record Rule(Access Allowed, string? Body)
    {
        public bool IsPublic => Allowed.Kind == "public";
        public bool IsDevice => Allowed.Kind == "device";
    }

    private static readonly Access PUBLIC = new("public", new HashSet<Role>());
    /// <summary>POS routes: a device credential is required, so no user token is ever enough (B4 contract §1).</summary>
    private static readonly Access DEVICE = new("device", new HashSet<Role>());
    private static readonly Access EVERYONE = new("roles", Enum.GetValues<Role>().ToHashSet());

    private static Access Roles(params Role[] roles) => new("roles", roles.ToHashSet());

    public static IReadOnlyDictionary<string, Rule> Matrix { get; } = Build();

    private static Dictionary<string, Rule> Build()
    {
        var matrix = new Dictionary<string, Rule>();
        void Add(string endpoint, Rule rule) => matrix.Add(endpoint, rule);


        var login = "{\"email\":\"matrix@zawed.test\",\"password\":\"matrix password\"}";
        var challenge = "{\"challengeToken\":\"x\"}";
        var challengeCode = "{\"challengeToken\":\"x\",\"code\":\"123456\"}";
        Add("GET /api/v1/meta", new Rule(PUBLIC, null));
        Add("POST /api/v1/auth/login", new Rule(PUBLIC, login));
        Add("POST /api/v1/auth/mfa/verify", new Rule(PUBLIC, challengeCode));
        Add("POST /api/v1/auth/mfa/enroll/start", new Rule(PUBLIC, challenge));
        Add("POST /api/v1/auth/mfa/enroll/confirm", new Rule(PUBLIC, challengeCode));
        Add("POST /api/v1/auth/refresh", new Rule(PUBLIC, null));
        Add("POST /api/v1/auth/logout", new Rule(PUBLIC, null));
        Add("POST /api/v1/setup/super-admin", new Rule(PUBLIC,
                "{\"setupToken\":\"x\",\"email\":\"s@zawed.test\",\"fullName\":\"S\",\"password\":\"a long setup password\"}"));
        Add("GET /api/v1/auth/me", new Rule(EVERYONE, null));
        Add("POST /api/v1/auth/password", new Rule(EVERYONE,
                "{\"currentPassword\":\"not the current one\",\"newPassword\":\"a new long password here\"}"));
        Add("GET /api/v1/auth/sessions", new Rule(EVERYONE, null));
        Add("DELETE /api/v1/auth/sessions/{id}", new Rule(EVERYONE, null));
        Add("GET /api/v1/admin/users", new Rule(Roles(Role.SUPER_ADMIN, Role.OPS_MANAGER, Role.OPS), null));
        Add("POST /api/v1/admin/users", new Rule(Roles(Role.SUPER_ADMIN), null));
        Add("POST /api/v1/admin/users/{id}/reset-password", new Rule(Roles(Role.SUPER_ADMIN, Role.OPS_MANAGER, Role.OPS), null));
        Add("POST /api/v1/admin/users/{id}/unlock", new Rule(Roles(Role.SUPER_ADMIN, Role.OPS_MANAGER, Role.OPS), null));
        Add("POST /api/v1/admin/users/{id}/status",
                new Rule(Roles(Role.SUPER_ADMIN), "{\"status\":\"SUSPENDED\",\"reason\":\"matrix\"}"));
        Add("POST /api/v1/admin/users/{id}/mfa/reset", new Rule(Roles(Role.SUPER_ADMIN), "{\"reason\":\"matrix\"}"));
        Add("GET /api/v1/admin/audit", new Rule(Roles(Role.SUPER_ADMIN), null));

        // ------------------------------------------------------------------ B2 master data
        var platform = Roles(Role.SUPER_ADMIN, Role.OPS_MANAGER, Role.OPS);
        var superAdmin = Roles(Role.SUPER_ADMIN);
        var company = Roles(Role.FLEET_MANAGER, Role.ACCOUNTANT);
        var station = Roles(Role.STATION_MANAGER);
        var reason = "\"reason\":\"matrix\"";
        var names = "\"legalName\":\"M\",\"nameAr\":\"M\",\"nameEn\":\"M\"";
        var vehicle = "{\"plate\":\"MTX 1\",\"make\":\"M\",\"model\":\"M\",\"fuelType\":\"GAS_92\","
                + "\"tankCapacityMl\":50000,\"tier\":\"NORMAL\"}";
        var stationBody = "{\"nameAr\":\"M\",\"nameEn\":\"M\",\"connectivity\":\"NORMAL\",\"fuelTypes\":[\"GAS_92\"]}";
        var unknown = "\"" + Guid.NewGuid() + "\"";

        Add("GET /api/v1/ops/companies", new Rule(platform, null));
        Add("POST /api/v1/ops/companies",
                new Rule(platform, "{\"company\":{" + names + "},\"lowBalanceThresholdPiastres\":0}"));
        Add("GET /api/v1/ops/companies/{id}", new Rule(platform, null));
        Add("PUT /api/v1/ops/companies/{id}", new Rule(platform, "{" + names + "}"));
        Add("PUT /api/v1/ops/companies/{id}/policy",
                new Rule(superAdmin, "{\"cooldownMinutes\":180,\"lowBalanceThresholdPiastres\":0," + reason + "}"));
        Add("POST /api/v1/ops/companies/{id}/status", new Rule(platform, "{\"status\":\"FROZEN\"," + reason + "}"));
        Add("GET /api/v1/ops/companies/{id}/departments", new Rule(platform, null));
        Add("POST /api/v1/ops/companies/{id}/departments", new Rule(platform, "{\"name\":\"Matrix\"}"));
        Add("GET /api/v1/company/profile", new Rule(company, null));
        Add("GET /api/v1/company/departments", new Rule(company, null));

        Add("GET /api/v1/ops/vehicles", new Rule(platform, null));
        Add("POST /api/v1/ops/vehicles", new Rule(platform, "{\"companyId\":" + unknown + ",\"vehicle\":" + vehicle + "}"));
        Add("GET /api/v1/ops/vehicles/{id}", new Rule(platform, null));
        Add("PUT /api/v1/ops/vehicles/{id}", new Rule(platform, vehicle));
        Add("POST /api/v1/ops/vehicles/{id}/status", new Rule(platform, "{\"status\":\"SUSPENDED\"," + reason + "}"));
        Add("POST /api/v1/ops/vehicles/{id}/tags", new Rule(platform, "{\"uid\":\"04A1B2C3\"}"));
        Add("POST /api/v1/ops/tags/{id}/status", new Rule(platform, "{\"status\":\"LOST\"," + reason + "}"));
        Add("PUT /api/v1/ops/vehicles/{id}/limits",
                new Rule(platform, "{\"kind\":\"VOLUME\",\"period\":\"DAY\",\"maxValue\":1000}"));
        Add("DELETE /api/v1/ops/vehicles/{id}/limits/{limitId}", new Rule(platform, null));
        Add("GET /api/v1/company/vehicles", new Rule(company, null));
        Add("GET /api/v1/company/vehicles/{id}", new Rule(company, null));

        Add("GET /api/v1/ops/operators", new Rule(platform, null));
        Add("POST /api/v1/ops/operators", new Rule(platform, "{" + names + "}"));
        Add("GET /api/v1/ops/operators/{id}", new Rule(platform, null));
        Add("PUT /api/v1/ops/operators/{id}", new Rule(platform, "{" + names + "}"));
        Add("POST /api/v1/ops/operators/{id}/status", new Rule(platform, "{\"status\":\"SUSPENDED\"," + reason + "}"));
        Add("GET /api/v1/ops/stations", new Rule(platform, null));
        Add("POST /api/v1/ops/stations",
                new Rule(platform, "{\"operatorId\":" + unknown + ",\"station\":" + stationBody + "}"));
        Add("GET /api/v1/ops/stations/{id}", new Rule(platform, null));
        Add("PUT /api/v1/ops/stations/{id}", new Rule(platform, stationBody));
        Add("POST /api/v1/ops/stations/{id}/status", new Rule(platform, "{\"status\":\"SUSPENDED\"," + reason + "}"));
        Add("GET /api/v1/ops/devices", new Rule(platform, null));
        Add("POST /api/v1/ops/devices", new Rule(platform, "{\"stationId\":" + unknown + "}"));
        Add("GET /api/v1/ops/devices/{id}", new Rule(platform, null));
        Add("POST /api/v1/ops/devices/{id}/activation-code", new Rule(platform, null));
        Add("POST /api/v1/ops/devices/{id}/revoke", new Rule(platform, "{" + reason + "}"));
        Add("GET /api/v1/ops/agents", new Rule(platform, null));
        Add("POST /api/v1/ops/agents", new Rule(platform, "{\"stationId\":" + unknown + ",\"fullName\":\"M\"}"));
        Add("GET /api/v1/ops/agents/{id}", new Rule(platform, null));
        Add("POST /api/v1/ops/agents/{id}/reset-pin", new Rule(platform, null));
        Add("POST /api/v1/ops/agents/{id}/status", new Rule(platform, "{\"status\":\"SUSPENDED\"," + reason + "}"));
        Add("GET /api/v1/station/operator", new Rule(station, null));
        Add("GET /api/v1/station/stations", new Rule(station, null));
        Add("GET /api/v1/station/stations/{id}", new Rule(station, null));
        Add("GET /api/v1/station/devices", new Rule(station, null));
        Add("GET /api/v1/station/agents", new Rule(station, null));

        Add("GET /api/v1/ops/prices/current", new Rule(platform, null));
        Add("GET /api/v1/ops/prices", new Rule(platform, null));
        // An unknown station: allowed callers get 400 and no price is actually scheduled.
        Add("POST /api/v1/ops/prices",
                new Rule(platform, "{\"fuelType\":\"GAS_92\",\"stationId\":" + unknown + ",\"pricePiastres\":1500}"));

        // ------------------------------------------------------------ B3 ledger and wallet
        // The matrix users' companies have no row, so a top-up request answers 404 and credits nothing.
        var topUp = "{\"amountPiastres\":1000,\"bankReference\":\"MTX-" + Guid.NewGuid()
                + "\",\"transferDate\":\"2026-01-01\"}";
        Add("GET /api/v1/company/wallet", new Rule(company, null));
        Add("GET /api/v1/company/wallet/statement", new Rule(company, null));
        Add("GET /api/v1/company/topups", new Rule(company, null));
        Add("POST /api/v1/company/topups", new Rule(company, topUp));
        Add("POST /api/v1/company/topups/{id}/cancel", new Rule(company, null));
        Add("GET /api/v1/ops/topups", new Rule(platform, null));
        Add("GET /api/v1/ops/topups/{id}", new Rule(platform, null));
        var approvers = Roles(Role.SUPER_ADMIN, Role.OPS_MANAGER);
        Add("GET /api/v1/ops/topups/receiving-accounts", new Rule(platform, null));
        Add("POST /api/v1/ops/topups/{id}/verify", new Rule(platform, "{\"receivedAmountPiastres\":1000}"));
        Add("POST /api/v1/ops/topups/{id}/approve", new Rule(approvers, null));
        Add("GET /api/v1/ops/wallet-adjustments", new Rule(platform, null));
        Add("POST /api/v1/ops/companies/{id}/wallet/adjustments",
                new Rule(platform, "{\"amountPiastres\":1000,\"reason\":\"matrix\"}"));
        Add("POST /api/v1/ops/wallet-adjustments/{id}/approve", new Rule(approvers, null));
        Add("POST /api/v1/ops/wallet-adjustments/{id}/reject", new Rule(approvers, "{" + reason + "}"));
        Add("POST /api/v1/ops/wallet-adjustments/{id}/cancel", new Rule(platform, null));
        Add("POST /api/v1/ops/topups/{id}/reject", new Rule(platform, "{" + reason + "}"));
        Add("GET /api/v1/ops/companies/{id}/wallet", new Rule(platform, null));
        Add("GET /api/v1/ops/companies/{id}/wallet/statement", new Rule(platform, null));
        Add("POST /api/v1/ops/companies/{id}/wallet/credits", new Rule(platform, topUp));
        Add("GET /api/v1/ops/reconciliation/runs", new Rule(platform, null));
        Add("POST /api/v1/ops/reconciliation/runs", new Rule(superAdmin, null));
        Add("GET /api/v1/ops/reconciliation/runs/{id}/findings", new Rule(platform, null));

        // ------------------------------------------------------------------ B7 W4 scheduled jobs (ADR 0028)
        Add("GET /api/v1/ops/jobs", new Rule(platform, null));
        Add("GET /api/v1/ops/jobs/runs", new Rule(platform, null));

        // ---------------------------------------------------------------------- B4 fueling
        Add("POST /api/v1/pos/activate", new Rule(PUBLIC,
                "{\"deviceCode\":\"POS-MATRIX\",\"activationCode\":\"MATRIXCODE12\",\"publicKey\":\"bm90IGEga2V5\"}"));
        Add("POST /api/v1/pos/token", new Rule(PUBLIC, "{\"deviceId\":" + unknown + ",\"timestamp\":"
                + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ",\"nonce\":\"matrix-nonce-0001\",\"signature\":\"eA==\"}"));
        Add("POST /api/v1/pos/agents/login", new Rule(DEVICE, "{\"workerId\":\"W0000000\",\"pin\":\"000000\"}"));
        Add("POST /api/v1/pos/agents/pin", new Rule(DEVICE, "{\"currentPin\":\"000000\",\"newPin\":\"482915\"}"));
        Add("POST /api/v1/pos/agents/logout", new Rule(DEVICE, null));
        Add("POST /api/v1/pos/fuelings", new Rule(DEVICE, "{\"clientTxnId\":" + unknown
                + ",\"tagUid\":\"04A1B2C3\",\"volumeMl\":1000,\"deviceAt\":\"2026-01-01T00:00:00Z\",\"receiptLocale\":\"ar\"}"));
        Add("GET /api/v1/receipts/{id}", new Rule(PUBLIC, null));

        // ------------------------------------------------------------------ B5 offline sync
        Add("GET /api/v1/pos/snapshot", new Rule(DEVICE, null));
        Add("POST /api/v1/pos/fuelings/{clientTxnId}/resolve", new Rule(DEVICE, "{\"toldOutcome\":\"REFUSED\"}"));
        Add("POST /api/v1/pos/sync", new Rule(DEVICE, "{\"batchId\":" + unknown + ",\"pending\":0,\"items\":[]}"));
        Add("GET /api/v1/ops/sync/devices", new Rule(platform, null));
        Add("GET /api/v1/ops/sync/batches", new Rule(platform, null));
        Add("GET /api/v1/station/sync/devices", new Rule(station, null));
        Add("GET /api/v1/ops/fuelings", new Rule(platform, null));
        Add("GET /api/v1/ops/fuelings/{id}", new Rule(platform, null));
        Add("GET /api/v1/ops/alerts", new Rule(platform, null));
        Add("POST /api/v1/ops/alerts/{id}/acknowledge", new Rule(platform, null));
        Add("GET /api/v1/company/fuelings", new Rule(company, null));
        Add("GET /api/v1/company/fuelings/{id}", new Rule(company, null));
        Add("GET /api/v1/company/alerts", new Rule(company, null));
        Add("GET /api/v1/station/fuelings", new Rule(station, null));
        Add("GET /api/v1/station/fuelings/{id}", new Rule(station, null));

        // ------------------------------------------------------- B6 exports, calendar, shifts
        // No range: an allowed caller gets 400 and nothing is read.
        Add("GET /api/v1/ops/fuelings/export", new Rule(platform, null));
        Add("GET /api/v1/company/fuelings/export", new Rule(company, null));
        Add("GET /api/v1/station/fuelings/export", new Rule(station, null));
        Add("GET /api/v1/ops/activity", new Rule(platform, null));
        Add("GET /api/v1/company/activity", new Rule(company, null));
        Add("GET /api/v1/station/activity", new Rule(station, null));
        Add("GET /api/v1/ops/shifts", new Rule(platform, null));
        Add("GET /api/v1/station/shifts", new Rule(station, null));

        // ---------------------------------------------------------------- B6 station finance
        // Unknown stations, payouts, changes and reversals: allowed callers get 404 and nothing moves.
        var bankChange = "{\"accountNumber\":\"EG380019000500000000263180002\",\"bankName\":\"M\",\"holderName\":\"M\","
                + "\"bankLetterReference\":\"MTX-1\"}";
        var payout = "{\"amountPiastres\":1000,\"bankReference\":\"MTX-" + Guid.NewGuid()
                + "\",\"valueDate\":\"2026-01-01\"}";
        Add("GET /api/v1/ops/stations/{id}/payable", new Rule(platform, null));
        Add("GET /api/v1/ops/stations/{id}/statement", new Rule(platform, null));
        Add("GET /api/v1/ops/stations/{id}/statement/summary", new Rule(platform, null));
        Add("GET /api/v1/ops/stations/{id}/bank-account", new Rule(platform, null));
        Add("POST /api/v1/ops/stations/{id}/bank-account/changes", new Rule(platform, bankChange));
        Add("GET /api/v1/ops/bank-account-changes", new Rule(platform, null));
        Add("POST /api/v1/ops/bank-account-changes/{id}/approve", new Rule(platform, null));
        Add("POST /api/v1/ops/bank-account-changes/{id}/reject", new Rule(platform, "{" + reason + "}"));
        Add("GET /api/v1/ops/payouts", new Rule(platform, null));
        Add("GET /api/v1/ops/payouts/{id}", new Rule(platform, null));
        Add("POST /api/v1/ops/stations/{id}/payouts", new Rule(platform, payout));
        Add("POST /api/v1/ops/payouts/{id}/approve", new Rule(platform, null));
        Add("POST /api/v1/ops/payouts/{id}/cancel", new Rule(platform, "{" + reason + "}"));
        Add("GET /api/v1/ops/payable-holds", new Rule(platform, null));
        Add("POST /api/v1/ops/fuelings/{id}/payable-hold/release", new Rule(platform, "{" + reason + "}"));
        Add("POST /api/v1/ops/fuelings/{id}/reversals", new Rule(platform, "{" + reason + "}"));
        Add("GET /api/v1/ops/reversals", new Rule(platform, null));
        Add("GET /api/v1/ops/reversals/{id}", new Rule(platform, null));
        Add("POST /api/v1/ops/reversals/{id}/approve", new Rule(platform, null));
        Add("POST /api/v1/ops/reversals/{id}/reject", new Rule(platform, "{" + reason + "}"));
        Add("GET /api/v1/station/stations/{id}/payable", new Rule(station, null));
        Add("GET /api/v1/station/stations/{id}/statement", new Rule(station, null));
        Add("GET /api/v1/station/stations/{id}/statement/summary", new Rule(station, null));
        Add("GET /api/v1/station/stations/{id}/bank-account", new Rule(station, null));
        Add("GET /api/v1/station/payouts", new Rule(station, null));
        Add("GET /api/v1/station/reversals", new Rule(station, null));
        Add("GET /api/v1/company/reversals", new Rule(company, null));

        // -------------------------------------------------------------- fleet import, onboarding
        // Unknown jobs answer 404; the upload without a file answers 400 IMPORT_FILE_REQUIRED.
        Add("GET /api/v1/ops/fleet-imports/template", new Rule(platform, null));
        Add("POST /api/v1/ops/companies/{id}/fleet-imports", new Rule(platform, "{}"));
        Add("GET /api/v1/ops/fleet-imports", new Rule(platform, null));
        Add("GET /api/v1/ops/fleet-imports/{id}", new Rule(platform, null));
        Add("GET /api/v1/ops/fleet-imports/{id}/rows", new Rule(platform, null));
        Add("GET /api/v1/ops/fleet-imports/{id}/errors.csv", new Rule(platform, null));
        Add("POST /api/v1/ops/fleet-imports/{id}/confirm", new Rule(platform, "{}"));
        Add("POST /api/v1/ops/fleet-imports/{id}/cancel", new Rule(platform, null));
        Add("GET /api/v1/ops/companies/{id}/onboarding", new Rule(platform, null));

        // ---------------------------------------------------------------------- B6 tickets
        // Each side's body asks for a type the other side owns, so an allowed caller gets 400 and no
        // ticket is written for the matrix users' tenants (which have no rows).
        var ticketReply = "{\"body\":\"matrix\"}";
        Add("GET /api/v1/company/tickets", new Rule(company, null));
        Add("POST /api/v1/company/tickets",
                new Rule(company, "{\"type\":\"DEVICE_FAULT\",\"subject\":\"m\",\"body\":\"m\"}"));
        Add("GET /api/v1/company/tickets/{id}", new Rule(company, null));
        Add("POST /api/v1/company/tickets/{id}/messages", new Rule(company, ticketReply));
        Add("GET /api/v1/station/tickets", new Rule(station, null));
        Add("POST /api/v1/station/tickets",
                new Rule(station, "{\"type\":\"LOST_TAG\",\"subject\":\"m\",\"body\":\"m\"}"));
        Add("GET /api/v1/station/tickets/{id}", new Rule(station, null));
        Add("POST /api/v1/station/tickets/{id}/messages", new Rule(station, ticketReply));
        Add("GET /api/v1/ops/tickets", new Rule(platform, null));
        Add("GET /api/v1/ops/tickets/{id}", new Rule(platform, null));
        Add("POST /api/v1/ops/tickets/{id}/messages", new Rule(platform, ticketReply));
        Add("POST /api/v1/ops/tickets/{id}/status", new Rule(platform, "{\"status\":\"RESOLVED\"}"));

        // --------------------------------------------------------------- B7 notifications
        // Every signed-in user has their own feed (scope and role decide what is in it, not a
        // permission); an unknown notification answers 404.
        Add("GET /api/v1/notifications", new Rule(EVERYONE, null));
        Add("GET /api/v1/notifications/unread-count", new Rule(EVERYONE, null));
        Add("POST /api/v1/notifications/{id}/read", new Rule(EVERYONE, null));
        Add("POST /api/v1/notifications/read-all", new Rule(EVERYONE, null));
        // ------------------------------------------------------------------------- U1 leads
        // The public form: anyone, no token. An unknown lead answers 404 to allowed callers.
        Add("POST /api/v1/public/demo-requests", new Rule(PUBLIC, "{\"companyName\":\"Matrix Co\","
                + "\"contactName\":\"Matrix\",\"phone\":\"01000000001\",\"email\":\"matrix-" + Guid.NewGuid()
                + "@example.test\",\"fleetSize\":\"1-19\",\"locale\":\"en\",\"website\":\"\",\"startedAt\":"
                + DateTimeOffset.UtcNow.AddSeconds(-60).ToUnixTimeMilliseconds() + "}"));
        Add("GET /api/v1/ops/leads", new Rule(platform, null));
        Add("PATCH /api/v1/ops/leads/{id}", new Rule(platform, "{\"status\":\"CONTACTED\"}"));

        return matrix;
    }

    /// <summary>
    /// Every controller endpoint of the running application, as <c>"METHOD /api/v1/path/{var}"</c>. The health probes
    /// (minimal endpoints, Spring's actuator in Java) and the OpenAPI document are not in the table, as in Java.
    /// </summary>
    public static SortedSet<string> RegisteredEndpoints(IServiceProvider services)
    {
        var registered = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            if (endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not { } action
                || action.ControllerTypeInfo.Assembly != typeof(Program).Assembly)
            {
                continue;
            }
            var path = "/" + Constraint().Replace(endpoint.RoutePattern.RawText!.TrimStart('/'), "{$1}");
            if (!path.StartsWith("/api/v1", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            {
                registered.Add($"{method} {path}");
            }
        }
        return registered;
    }

    [GeneratedRegex(@"\{([^}:=?]+)[^}]*\}")]
    private static partial Regex Constraint();

    /// <summary>The Java operations the .NET API does not serve yet (the OpenAPI parity list).</summary>
    public static IReadOnlySet<string> Pending() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "OpenApi", "pending-operations.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet();

    /// <summary>The roles the matrix allows on an endpoint (empty for a public or a device route), or null if it has no row.</summary>
    public static IReadOnlySet<Role>? AllowedRoles(string endpoint) => Matrix.TryGetValue(endpoint, out var rule) ? rule.Allowed.Roles : null;

    public static bool IsDeviceRoute(string endpoint) => Matrix.TryGetValue(endpoint, out var rule) && rule.IsDevice;

    public static bool IsPublicRoute(string endpoint) => Matrix.TryGetValue(endpoint, out var rule) && rule.IsPublic;

    /// <summary>A body that passes validation, so the permission is what answers; null when the matrix sends none.</summary>
    public static string? ValidBody(string endpoint) =>
        Matrix.TryGetValue(endpoint, out var rule) ? rule.Body ?? CreateUserBody(endpoint) : null;

    private readonly IdentityTestSupport id = new(api.Factory);

    [Fact]
    public void EveryEndpointHasADecision()
    {
        var registered = RegisteredEndpoints(api.Factory.Services);
        var pending = Pending();
        Assert.Empty(registered.Where(e => !Matrix.ContainsKey(e)).Select(e => "endpoint without a row in the authorization matrix: " + e));
        Assert.Empty(Matrix.Keys.Where(e => !registered.Contains(e) && !pending.Contains(e))
            .Select(e => "row for an endpoint that is neither served nor pending: " + e));
    }

    [Fact]
    public async Task EachRoleIsAllowedExactlyWhatTheMatrixSays()
    {
        var tokens = await TokensAsync();
        var failures = new List<string>();
        var served = RegisteredEndpoints(api.Factory.Services);
        foreach (var (endpoint, rule) in Matrix.Where(m => served.Contains(m.Key)))
        {
            var method = endpoint[..endpoint.IndexOf(' ')];
            var path = PathVariable().Replace(endpoint[(method.Length + 1)..], _ => Guid.NewGuid().ToString());
            var body = rule.Body ?? CreateUserBody(endpoint);
            id.Limiter.Reset();
            var anonymous = await SendAsync(method, path, null, body);
            if (rule.IsPublic)
            {
                var code = await CodeOrNone(anonymous);
                if (code is "UNAUTHENTICATED" or "FORBIDDEN")
                {
                    failures.Add($"anonymous {endpoint}: {code}");
                }
                continue;
            }
            if ((int)anonymous.StatusCode != 401)
            {
                failures.Add($"anonymous {endpoint}: {(int)anonymous.StatusCode}");
            }
            foreach (var role in Enum.GetValues<Role>())
            {
                var response = await SendAsync(method, path, tokens[role], body);
                var code = await CodeOrNone(response);
                if (rule.IsDevice)
                {
                    if ((int)response.StatusCode != 401 || code != "DEVICE_UNAUTHENTICATED")
                    {
                        failures.Add($"{role} {endpoint}: {(int)response.StatusCode} {code}");
                    }
                }
                else if (rule.Allowed.Roles.Contains(role))
                {
                    if (code is "UNAUTHENTICATED" or "FORBIDDEN")
                    {
                        failures.Add($"{role} {endpoint}: refused with {code}");
                    }
                }
                else if ((int)response.StatusCode != 403 || code != "FORBIDDEN")
                {
                    failures.Add($"{role} {endpoint}: {(int)response.StatusCode} {code}, expected 403 FORBIDDEN");
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// QA round 1: a station token on an Ops payout answered 400 with the body's field names, because the body was read
    /// and validated before the permission. A caller without the permission gets 403 before the body, its JSON or the
    /// path is even looked at: an empty body, broken JSON, a fractional amount, an unknown field and a malformed id all
    /// answer FORBIDDEN, and the answer never names a field.
    /// </summary>
    [Fact]
    public async Task ARoleWithoutThePermissionIsRefusedBeforeTheBodyIsRead()
    {
        var tokens = await TokensAsync();
        string[] invalidBodies = ["{}", "{\"amountPiastres\":", "{\"amountPiastres\":100.5}", "{\"noSuchField\":1}", "[]"];
        var failures = new List<string>();
        var served = RegisteredEndpoints(api.Factory.Services);
        int checkedRequests = 0, expected = 0;
        foreach (var (endpoint, rule) in Matrix.Where(m => served.Contains(m.Key)))
        {
            var method = endpoint[..endpoint.IndexOf(' ')];
            if (rule.IsPublic || rule.IsDevice || method == "GET")
            {
                continue;
            }
            var template = endpoint[(method.Length + 1)..];
            var path = PathVariable().Replace(template, _ => Guid.NewGuid().ToString());
            var malformedPath = PathVariable().Replace(template, "not-a-uuid");
            foreach (var role in Enum.GetValues<Role>().Where(r => !rule.Allowed.Roles.Contains(r)))
            {
                id.Limiter.Reset(); // many requests per user: the per-user limit is not what is tested
                expected += invalidBodies.Length + (malformedPath == template ? 0 : 1);
                foreach (var body in invalidBodies)
                {
                    var response = await id.AuthedAsync(method, path, tokens[role], body);
                    var text = await response.Content.ReadAsStringAsync();
                    if ((int)response.StatusCode != 403 || text.Contains("\"fields\"", StringComparison.Ordinal))
                    {
                        failures.Add($"{role} {endpoint} with {body}: {(int)response.StatusCode} {text}");
                    }
                    checkedRequests++;
                }
                if (malformedPath != template)
                {
                    var response = await id.AuthedAsync(method, malformedPath, tokens[role], ValidBody(endpoint));
                    if ((int)response.StatusCode != 403)
                    {
                        failures.Add($"{role} {endpoint} with a malformed id: {(int)response.StatusCode}");
                    }
                    checkedRequests++;
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Equal(expected, checkedRequests);
        Assert.True(checkedRequests >= 120, $"requests checked: {checkedRequests}"); // identity alone; Java's whole API checked > 500
    }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex PathVariable();

    private async Task<Dictionary<Role, string>> TokensAsync()
    {
        var tokens = new Dictionary<Role, string>();
        foreach (var role in Enum.GetValues<Role>())
        {
            tokens[role] = (await id.SignInAsync(await id.SeedAsync(role))).AccessToken;
            id.Limiter.Reset(); // the helper's sign-ins must not spend the test's budget
        }
        return tokens;
    }

    private Task<HttpResponseMessage> SendAsync(string method, string path, string? token, string? body)
    {
        if (token is null)
        {
            return method == "GET" ? id.GetAsync(path) : id.PostAsync(path, body ?? "{}");
        }
        return id.AuthedAsync(method, path, token, method is "GET" or "DELETE" ? null : body);
    }

    private static string? CreateUserBody(string endpoint) => endpoint != "POST /api/v1/admin/users"
        ? null
        : $"{{\"email\":\"m-{Guid.NewGuid()}@zawed.test\",\"fullName\":\"M\",\"role\":\"ACCOUNTANT\",\"scope\":{{\"type\":\"COMPANY\",\"id\":\"{Guid.NewGuid()}\"}}}}";

    private static async Task<string> CodeOrNone(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!body.Contains("\"code\"", StringComparison.Ordinal))
        {
            return "(none)";
        }
        return JsonNode.Parse(body) is JsonObject json && json["code"] is JsonValue code ? code.GetValue<string>() : $"(none: {(int)response.StatusCode})";
    }
}
