using System.Text;
using System.Text.RegularExpressions;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Fleet;

public enum ImportFormat
{
    CSV,
    XLSX,
}

/// <summary>The columns of an import file, by their canonical name (Java <c>FleetImportFile.Column</c>).</summary>
public enum ImportColumn
{
    PLATE,
    FLEET_CODE,
    MAKE,
    MODEL,
    YEAR,
    FUEL_TYPE,
    TANK_L,
    DEPARTMENT,
    TIER,
    VIP_CEILING_L,
    DAILY_L,
    WEEKLY_L,
    MONTHLY_L,
}

/// <summary>A column's key, whether a file must have it, and the headers that mean it, in English or Arabic.</summary>
public sealed record ColumnInfo(ImportColumn Column, string Key, bool Required, IReadOnlyList<string> Headers)
{
    /// <summary>The Arabic header the template uses (the first header with an Arabic letter in the list).</summary>
    public string ArabicHeader => Headers.FirstOrDefault(h => h.Any(c => c is >= '؀' and <= 'ۿ')) ?? Key;
}

/// <summary>One data row: its number in the file (the header is row 1) and its cells by column, in file order.</summary>
public sealed record ImportRow(int Number, IReadOnlyList<KeyValuePair<ImportColumn, string>> Cells)
{
    public string? Get(ImportColumn column)
    {
        foreach (var (key, value) in Cells)
        {
            if (key == column)
            {
                return value;
            }
        }
        return null;
    }
}

/// <summary>
/// Reads an import file into rows of named columns, with hard limits and no surprises (ADR 0026; Java
/// <c>FleetImportFile</c>).
/// <list type="bullet">
/// <item>At most <see cref="MaxBytes"/> on the wire and <see cref="MaxRows"/> data rows: a larger file is refused with a clear
/// error, never truncated.</item>
/// <item>CSV: UTF-8 (a byte-order mark is fine), comma or semicolon, RFC 4180 quoting. Invalid UTF-8 is refused rather than
/// guessed.</item>
/// <item>XLSX: the archive is inspected before parsing (<see cref="Xlsx.Inspect"/>): at most <see cref="MaxEntries"/> entries
/// and <see cref="MaxUncompressed"/> bytes once inflated (a zip bomb stops here), and any XML with a <c>&lt;!DOCTYPE</c> or
/// <c>&lt;!ENTITY</c> is refused. Only the first sheet is read, as text.</item>
/// <item>Headers are matched by name, in English or Arabic, in any order; unknown columns are refused so a typo never
/// silently drops data.</item>
/// </list>
/// </summary>
public static partial class FleetImportFile
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxRows = 5_000;
    public const int MaxColumns = 30;
    public const int MaxCell = 200;
    public const int MaxEntries = 200;
    public const long MaxUncompressed = 60L * 1024 * 1024;

    public static readonly IReadOnlyList<ColumnInfo> Columns =
    [
        new(ImportColumn.PLATE, "plate", true, ["plate", "plate_number", "اللوحة", "رقم اللوحة"]),
        new(ImportColumn.FLEET_CODE, "fleet_code", false, ["fleet_code", "fleet code", "vehicle_code", "كود الأسطول", "الكود الداخلي", "كود العربية"]),
        new(ImportColumn.MAKE, "make", true, ["make", "brand", "الماركة", "الشركة المصنعة"]),
        new(ImportColumn.MODEL, "model", true, ["model", "الموديل"]),
        new(ImportColumn.YEAR, "year", false, ["year", "model_year", "سنة الصنع", "الموديل سنة"]),
        new(ImportColumn.FUEL_TYPE, "fuel_type", true, ["fuel_type", "fuel", "نوع الوقود", "الوقود"]),
        new(ImportColumn.TANK_L, "tank_capacity_l", true, ["tank_capacity_l", "tank_l", "tank", "سعة التانك (لتر)", "سعة التانك", "سعة الخزان"]),
        new(ImportColumn.DEPARTMENT, "department", false, ["department", "الإدارة", "القسم"]),
        new(ImportColumn.TIER, "tier", false, ["tier", "الفئة"]),
        new(ImportColumn.VIP_CEILING_L, "vip_monthly_ceiling_l", false, ["vip_monthly_ceiling_l", "سقف VIP الشهري (لتر)"]),
        new(ImportColumn.DAILY_L, "daily_limit_l", false, ["daily_limit_l", "حد يومي (لتر)", "الحد اليومي"]),
        new(ImportColumn.WEEKLY_L, "weekly_limit_l", false, ["weekly_limit_l", "حد أسبوعي (لتر)", "الحد الأسبوعي"]),
        new(ImportColumn.MONTHLY_L, "monthly_limit_l", false, ["monthly_limit_l", "حد شهري (لتر)", "الحد الشهري"]),
    ];

    public static ColumnInfo Info(ImportColumn column) => Columns[(int)column];

    public static ImportFormat FormatOf(string? fileName, byte[] bytes)
    {
        var zip = bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4;
        var name = (fileName ?? "").ToLowerInvariant();
        if (name.EndsWith(".xlsx", StringComparison.Ordinal) && zip)
        {
            return ImportFormat.XLSX;
        }
        if (name.EndsWith(".csv", StringComparison.Ordinal) && !zip)
        {
            return ImportFormat.CSV;
        }
        throw new ApiException(400, "IMPORT_FILE_TYPE", "Upload a .csv or .xlsx file", "file");
    }

    public static List<ImportRow> Read(ImportFormat format, byte[] bytes, ILogger? log = null)
    {
        if (bytes.Length == 0)
        {
            throw new ApiException(400, "IMPORT_FILE_EMPTY", "The file is empty", "file");
        }
        if (bytes.Length > MaxBytes)
        {
            throw TooLarge();
        }
        var grid = format == ImportFormat.CSV ? Csv(bytes) : Xlsx.Read(bytes, log);
        if (grid.Count == 0)
        {
            throw new ApiException(400, "IMPORT_FILE_EMPTY", "The file has no header row", "file");
        }
        var columns = Header(grid[0]);
        var rows = new List<ImportRow>();
        for (var i = 1; i < grid.Count; i++)
        {
            var cells = grid[i];
            if (cells.All(c => c is null || JavaText.IsBlank(c)))
            {
                continue; // blank lines carry nothing
            }
            var named = new List<KeyValuePair<ImportColumn, string>>();
            for (var c = 0; c < columns.Count; c++)
            {
                var value = c < cells.Count ? Clean(cells[c]) : null;
                if (value is not null)
                {
                    named.RemoveAll(kv => kv.Key == columns[c]);
                    named.Add(new(columns[c], value));
                }
            }
            rows.Add(new ImportRow(i + 1, named));
        }
        if (rows.Count == 0)
        {
            throw new ApiException(400, "IMPORT_FILE_EMPTY", "The file has no vehicle rows", "file");
        }
        if (rows.Count > MaxRows)
        {
            throw TooManyRows();
        }
        return rows;
    }

    private static List<ImportColumn> Header(IReadOnlyList<string?> raw)
    {
        if (raw.Count > MaxColumns)
        {
            throw new ApiException(400, "IMPORT_HEADER_INVALID", "Too many columns", "file");
        }
        var columns = new List<ImportColumn>();
        var unknown = new List<string>();
        foreach (var cell in raw)
        {
            var name = Normalize(cell);
            ColumnInfo? match = null;
            foreach (var column in Columns)
            {
                if (column.Headers.Any(h => Normalize(h) == name))
                {
                    match = column; // the last column that claims the header, as the Java loop did
                }
            }
            if (match is null)
            {
                unknown.Add(cell is null ? "" : JavaText.Strip(cell));
                continue;
            }
            if (columns.Contains(match.Column))
            {
                throw new ApiException(400, "IMPORT_HEADER_INVALID", "A column appears twice: " + match.Key, "file",
                    new Dictionary<string, object> { ["column"] = match.Key });
            }
            columns.Add(match.Column);
        }
        if (unknown.Count > 0)
        {
            throw new ApiException(400, "IMPORT_HEADER_INVALID", "Unknown columns; use the template's headers", "file",
                new Dictionary<string, object> { ["unknown"] = unknown });
        }
        var missing = Columns.Where(c => c.Required && !columns.Contains(c.Column)).Select(c => c.Key).ToList();
        if (missing.Count > 0)
        {
            throw new ApiException(400, "IMPORT_HEADER_INVALID", "Required columns are missing", "file",
                new Dictionary<string, object> { ["missing"] = missing });
        }
        return columns;
    }

    /// <summary>Header comparison: NFKC, case-folded, spaces, underscores and dashes ignored.</summary>
    public static string Normalize(string? value)
    {
        if (value is null)
        {
            return "";
        }
        var folded = JavaText.Strip(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant());
        if (folded.Length > 0 && folded[0] == '﻿')
        {
            folded = folded[1..];
        }
        return Separators().Replace(folded, "");
    }

    private static string? Clean(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var stripped = JavaText.Strip(value);
        if (stripped.Length == 0)
        {
            return null;
        }
        return stripped.Length > MaxCell ? stripped[..(MaxCell + 1)] : stripped; // over-long is an error later
    }

    // --------------------------------------------------------------------------------------------------------- CSV

    private static List<List<string?>> Csv(byte[] bytes)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new ApiException(400, "IMPORT_FILE_ENCODING", "Save the CSV as UTF-8 (Excel: CSV UTF-8)", "file");
        }
        if (text.StartsWith('﻿'))
        {
            text = text[1..];
        }
        var firstLineEnd = text.IndexOf('\n');
        var firstLine = firstLineEnd < 0 ? text : text[..firstLineEnd];
        var delimiter = !firstLine.Contains(',') && firstLine.Contains(';') ? ';' : ',';
        var rows = new List<List<string?>>();
        var row = new List<string?>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
                GuardRows(rows.Count);
            }
            else
            {
                cell.Append(c);
            }
            if (cell.Length > 10_000)
            {
                throw new ApiException(400, "IMPORT_FILE_MALFORMED", "A cell is far too long", "file");
            }
        }
        if (quoted)
        {
            throw new ApiException(400, "IMPORT_FILE_MALFORMED", "A quoted value is never closed", "file");
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        return rows;
    }

    internal static void GuardRows(int rows)
    {
        if (rows > MaxRows + 1 + 1000) // generous room for blank lines, then stop reading at once
        {
            throw TooManyRows();
        }
    }

    private static ApiException TooManyRows() => new(400, "IMPORT_TOO_MANY_ROWS", "A file may hold at most 5,000 vehicles; split it", "file",
        new Dictionary<string, object> { ["maxRows"] = MaxRows });

    internal static ApiException TooLarge() => new(413, "IMPORT_FILE_TOO_LARGE", "A file may be at most 5 MB", "file",
        new Dictionary<string, object> { ["maxBytes"] = MaxBytes });

    /// <summary>Java's <c>[\s_\-]+</c>: <c>\s</c> is ASCII whitespace there.</summary>
    [GeneratedRegex("[ \t\n\u000B\f\r_\\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();
}
