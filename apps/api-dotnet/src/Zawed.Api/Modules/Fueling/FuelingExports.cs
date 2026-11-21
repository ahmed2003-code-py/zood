using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Zawed.Api.Platform.Persistence;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Fueling;

/// <summary>An export's range: from the start of the first Cairo day to the start of the day after the last.</summary>
public sealed record ExportRange(DateTimeOffset From, DateTimeOffset To);

/// <summary>
/// Fuelings as a CSV file for a spreadsheet (roadmap delta: "transaction export"; Java <c>FuelingExports</c>). Runs in the
/// caller's scope, so row-level security limits the file exactly as it limits the list: a company exports its own fuelings,
/// an operator those at its stations.
/// <para>The file is UTF-8 with a byte-order mark, so Excel shows Arabic names and plates correctly. Money is in pounds with
/// two decimals and volume in litres with two decimals, for a person to read; times are in Africa/Cairo.</para>
/// </summary>
public sealed partial class FuelingExports(IDbSession session)
{
    /// <summary>A year at most, so one request cannot read the whole history.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    /// <summary>Far above a month of a large fleet; past it the range must be narrowed.</summary>
    public const int MaxRows = 50_000;

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private static readonly string[] HeaderEn = ["Reference", "Time (Cairo)", "Plate", "Company", "Station", "Fuel", "Litres",
        "Price per litre (EGP)", "Amount (EGP)", "Billable", "Status", "Decline reason", "Origin", "Conflicts", "Agent"];

    private static readonly string[] HeaderAr = ["المرجع", "الوقت (القاهرة)", "اللوحة", "الشركة", "المحطة", "الوقود", "اللترات",
        "سعر اللتر (جنيه)", "المبلغ (جنيه)", "محسوب", "الحالة", "سبب الرفض", "المصدر", "التعارضات", "العامل"];

    private const string Filters = """
        where evaluated_at >= @from and evaluated_at < @to
          and (cast(@company as uuid) is null or company_id = @company)
          and (cast(@operator as uuid) is null or operator_id = @operator)
          and (cast(@station as uuid) is null or station_id = @station)
          and (cast(@vehicle as uuid) is null or vehicle_id = @vehicle)
          and (cast(@status as text) is null or status = @status)
        """;

    private sealed class Row
    {
        public string Reference { get; set; } = "";
        public DateTime Evaluated_At { get; set; }
        public string Plate { get; set; } = "";
        public string Company_Name_Ar { get; set; } = "";
        public string Company_Name_En { get; set; } = "";
        public string Station_Name_Ar { get; set; } = "";
        public string Station_Name_En { get; set; } = "";
        public string Fuel_Type { get; set; } = "";
        public long Volume_Ml { get; set; }
        public long Unit_Price_Piastres { get; set; }
        public long Amount_Piastres { get; set; }
        public bool Billable { get; set; }
        public string Status { get; set; } = "";
        public string? Decline_Reason { get; set; }
        public string Origin { get; set; } = "";
        public string[] Conflicts { get; set; } = [];
        public string Agent_Worker_Id { get; set; } = "";
    }

    /// <summary>The range of two Cairo calendar days, inclusive, checked as the Java <c>Range</c> record checked it.</summary>
    public static Result<ExportRange> Range(DateOnly? from, DateOnly? to)
    {
        DateTimeOffset? start = from is { } f ? Zawed.Domain.Rules.Periods.AtStartOfDay(f, Activity.Cairo) : null;
        DateTimeOffset? end = to is { } t ? Zawed.Domain.Rules.Periods.AtStartOfDay(t.AddDays(1), Activity.Cairo) : null;
        if (start is null || end is null)
        {
            return Result<ExportRange>.Fail(400, ErrorCodes.ValidationFailed, "Both from and to are required", "from");
        }
        if (end <= start)
        {
            return Result<ExportRange>.Fail(400, ErrorCodes.ValidationFailed, "to must be after from", "to");
        }
        if (end.Value - start.Value > MaxRange)
        {
            return Result<ExportRange>.Fail(400, "EXPORT_RANGE_TOO_LONG", "Export at most a year at a time", "to",
                new Dictionary<string, object> { ["maxDays"] = (long)MaxRange.Days });
        }
        return new ExportRange(start.Value, end.Value);
    }

    /// <param name="arabic">headers and names in Arabic instead of English</param>
    public async Task<Result<byte[]>> CsvAsync(ExportRange range, Guid? companyId, Guid? operatorId, Guid? stationId, Guid? vehicleId,
        string? status, bool arabic, CancellationToken ct)
    {
        var tx = session.Current;
        var args = new
        {
            from = range.From.UtcDateTime,
            to = range.To.UtcDateTime,
            company = companyId,
            @operator = operatorId,
            station = stationId,
            vehicle = vehicleId,
            status,
        };
        var rows = await tx.QuerySingleAsync<long>("select count(*) from fuelings\n" + Filters, args, ct);
        if (rows > MaxRows)
        {
            return Result<byte[]>.Fail(400, "EXPORT_TOO_LARGE", "Narrow the date range", "from",
                new Dictionary<string, object> { ["rows"] = rows, ["maxRows"] = MaxRows });
        }
        var output = new StringBuilder(256 * (int)rows + 512);
        Line(output, arabic ? HeaderAr : HeaderEn);
        foreach (var row in await tx.QueryAsync<Row>("""
            select reference, evaluated_at, plate, company_name_ar, company_name_en, station_name_ar, station_name_en,
                   fuel_type, volume_ml, unit_price_piastres, amount_piastres, billable, status, decline_reason, origin,
                   conflicts, agent_worker_id
            from fuelings
            """ + "\n" + Filters + "\norder by evaluated_at, reference", args, ct))
        {
            var cairo = TimeZoneInfo.ConvertTime(Fuelings.Utc(row.Evaluated_At), Activity.Cairo);
            Line(output, [row.Reference, cairo.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), row.Plate,
                arabic ? row.Company_Name_Ar : row.Company_Name_En, arabic ? row.Station_Name_Ar : row.Station_Name_En, row.Fuel_Type,
                Decimal(row.Volume_Ml, 1000), Decimal(row.Unit_Price_Piastres, 100), Decimal(row.Amount_Piastres, 100),
                row.Billable ? "yes" : "no", row.Status, row.Decline_Reason ?? "", row.Origin, string.Join(" ", row.Conflicts),
                row.Agent_Worker_Id]);
        }
        var text = Encoding.UTF8.GetBytes(output.ToString());
        return Result<byte[]>.Ok([.. Bom, .. text]);
    }

    /// <summary>An integer amount in its minor unit as a plain decimal: 31500 piastres → 315.00.</summary>
    public static string Decimal(long value, long unitsPerWhole)
    {
        var magnitude = value == long.MinValue ? throw new OverflowException() : Math.Abs(value);
        var whole = magnitude / unitsPerWhole;
        var fraction = magnitude % unitsPerWhole;
        var digits = unitsPerWhole == 1000 ? 3 : 2;
        var text = whole.ToString(CultureInfo.InvariantCulture) + "." + fraction.ToString("D" + digits, CultureInfo.InvariantCulture);
        if (digits == 3)
        {
            text = text[..^1]; // litres to two decimals; millilitre precision is not needed
        }
        return value < 0 ? "-" + text : text;
    }

    /// <summary>RFC 4180: quote a field holding a comma, quote or line break; double inner quotes.</summary>
    private static void Line(StringBuilder output, IReadOnlyList<string?> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                output.Append(',');
            }
            var field = fields[i] ?? "";
            // A leading =, +, - or @ would be read by a spreadsheet as a formula (CSV injection).
            if (field.Length > 0 && "=+-@".Contains(field[0], StringComparison.Ordinal) && !Number().IsMatch(field))
            {
                field = "'" + field;
            }
            if (field.Contains(',', StringComparison.Ordinal) || field.Contains('"', StringComparison.Ordinal)
                || field.Contains('\n', StringComparison.Ordinal) || field.Contains('\r', StringComparison.Ordinal))
            {
                output.Append('"').Append(field.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                output.Append(field);
            }
        }
        output.Append("\r\n");
    }

    /// <summary>Java's <c>-?\d+(\.\d+)?</c> under <c>String.matches</c>: the whole field, ASCII digits only.</summary>
    [GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Number();
}
