using Zawed.Api.Modules.Audit.Contracts;
using Zawed.Api.Platform.Security;
using Zawed.Domain;

namespace Zawed.Api.Modules.Network;

// The network's rows as the Java records read them (Operators.Operator, Operators.Station, Devices.Device, Agents.Agent),
// with Dapper's column names. Times come back from PostgreSQL as UTC.

public enum OperatorStatus
{
    ACTIVE,
    SUSPENDED,
}

public enum StationStatus
{
    ACTIVE,
    SUSPENDED,
    CLOSED,
}

public enum Connectivity
{
    NORMAL,
    LOW,
}

public enum DeviceStatus
{
    PENDING_ACTIVATION,
    ACTIVE,
    REVOKED,
}

public enum AgentStatus
{
    ACTIVE,
    SUSPENDED,
    DEACTIVATED,
}

internal static class Rows
{
    public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    public static AuditActor Actor(ZawedPrincipal actor) => new(actor.UserId, actor.Role);

    /// <summary>Java's <c>String.trim()</c>: every character up to U+0020 off both ends.</summary>
    public static string JavaTrim(string value) => value.Trim(JavaTrimmed);

    private static readonly char[] JavaTrimmed = [.. Enumerable.Range(0, 33).Select(c => (char)c)];

    /// <summary>Java's <c>String.strip()</c> (<see cref="Platform.Text.JavaText.Strip"/>).</summary>
    public static string JavaStrip(string value) => Platform.Text.JavaText.Strip(value);

    /// <summary>Java's <c>String.isBlank()</c>.</summary>
    public static bool JavaBlank(string? value) => value is null || JavaStrip(value).Length == 0;
}

internal sealed class OperatorRow
{
    public Guid Id { get; set; }
    public string Legal_Name { get; set; } = "";
    public string Name_Ar { get; set; } = "";
    public string Name_En { get; set; } = "";
    public string? Tax_Id { get; set; }
    public string? Commercial_Registration { get; set; }
    public string? Contact_Name { get; set; }
    public string? Contact_Phone { get; set; }
    public string? Contact_Email { get; set; }
    public string Status { get; set; } = "";
    public DateTime Created_At { get; set; }

    public OperatorResponse ToResponse() => new(Id, Legal_Name, Name_Ar, Name_En, Tax_Id, Commercial_Registration, Contact_Name,
        Contact_Phone, Contact_Email, Enum.Parse<OperatorStatus>(Status), Rows.Utc(Created_At));
}

internal sealed class StationRow
{
    /// <summary>
    /// The Java <c>select *</c> of <c>stations</c>, with the coordinates as their exact text (no floating point here).
    /// </summary>
    public const string Columns = "id, operator_id, name_ar, name_en, address, latitude::text as latitude, longitude::text as longitude, "
        + "time_zone, connectivity, fuel_types, status, created_at";

    public Guid Id { get; set; }
    public Guid Operator_Id { get; set; }
    public string Name_Ar { get; set; } = "";
    public string Name_En { get; set; } = "";
    public string? Address { get; set; }
    public string? Latitude { get; set; }
    public string? Longitude { get; set; }
    public string Time_Zone { get; set; } = "";
    public string Connectivity { get; set; } = "";
    public string[] Fuel_Types { get; set; } = [];
    public string Status { get; set; } = "";
    public DateTime Created_At { get; set; }

    public StationStatus StationStatus => Enum.Parse<StationStatus>(Status);

    /// <summary>In the enum's order, as Java's <c>EnumSet</c> iterates.</summary>
    public IReadOnlyList<FuelType> FuelTypes => Fuel_Types.Select(Enum.Parse<FuelType>).Distinct().Order().ToList();

    public StationResponse ToResponse() => new(Id, Operator_Id, Name_Ar, Name_En, Address, Coordinate.Parse(Latitude),
        Coordinate.Parse(Longitude), Time_Zone, Enum.Parse<Connectivity>(Connectivity), FuelTypes, StationStatus, Rows.Utc(Created_At));
}

internal sealed class DeviceRow
{
    public Guid Id { get; set; }
    public Guid Operator_Id { get; set; }
    public Guid Station_Id { get; set; }
    public string Code { get; set; } = "";
    public string? Label { get; set; }
    public string Status { get; set; } = "";
    public DateTime? Activation_Expires_At { get; set; }
    public DateTime? Bound_At { get; set; }
    public DateTime? Last_Sync_At { get; set; }
    public DateTime? Revoked_At { get; set; }
    public string? Revoked_Reason { get; set; }
    public DateTime Created_At { get; set; }

    public DeviceStatus DeviceStatus => Enum.Parse<DeviceStatus>(Status);

    /// <summary>Java <c>DeviceResponse.of</c>: waiting for activation and the code has expired.</summary>
    public DeviceResponse ToResponse(DateTimeOffset now)
    {
        var expires = Rows.Utc(Activation_Expires_At);
        var expired = DeviceStatus == DeviceStatus.PENDING_ACTIVATION && (expires is null || expires <= now);
        return new DeviceResponse(Id, Operator_Id, Station_Id, Code, Label, DeviceStatus, expires, expired, Rows.Utc(Bound_At),
            Rows.Utc(Last_Sync_At), Rows.Utc(Revoked_At), Revoked_Reason, Rows.Utc(Created_At));
    }
}

internal sealed class AgentRow
{
    public Guid Id { get; set; }
    public Guid Operator_Id { get; set; }
    public Guid Station_Id { get; set; }
    public string Worker_Id { get; set; } = "";
    public string Full_Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Pin_Hash { get; set; }
    public string Status { get; set; } = "";
    public bool Must_Change_Pin { get; set; }
    public int Failed_Pin_Count { get; set; }
    public DateTime? Locked_Until { get; set; }
    public DateTime Created_At { get; set; }

    public AgentStatus AgentStatus => Enum.Parse<AgentStatus>(Status);

    /// <summary>Java <c>AgentResponse.of</c>: locked now when the lock ends in the future.</summary>
    public AgentResponse ToResponse(DateTimeOffset now)
    {
        var locked = Rows.Utc(Locked_Until);
        return new AgentResponse(Id, Operator_Id, Station_Id, Worker_Id, Full_Name, Phone, AgentStatus, Must_Change_Pin, locked,
            locked is { } until && until > now, Rows.Utc(Created_At));
    }
}
