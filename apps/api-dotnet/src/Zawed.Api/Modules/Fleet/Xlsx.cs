using System.IO.Compression;
using System.Text;
using System.Xml;
using Zawed.Api.Platform.Web;

namespace Zawed.Api.Modules.Fleet;

/// <summary>
/// The XLSX side of the import (Java <c>FleetImportFile.xlsx</c> with <c>fastexcel-reader</c>, and the template written
/// with <c>fastexcel</c>). No spreadsheet library: <see cref="System.IO.Compression.ZipArchive"/> and an
/// <see cref="XmlReader"/> with DTDs prohibited and no resolver, after the archive passed <see cref="Inspect"/>. Only the
/// first sheet is read, every cell as its text: a shared or inline string as written, anything else (a number, a
/// boolean) as its raw value, which is what <c>fastexcel</c>'s <c>Cell.getText()</c> gives.
/// </summary>
internal static class Xlsx
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly XmlReaderSettings Safe = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersFromEntities = 0,
        CloseInput = true,
    };

    // ------------------------------------------------------------------------------------------------- reading

    public static List<List<string?>> Read(byte[] bytes, ILogger? log)
    {
        Inspect(bytes, log);
        var rows = new List<List<string?>>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            var workbook = Target(zip, "_rels/.rels", "", "/officeDocument") ?? "xl/workbook.xml";
            var folder = workbook.Contains('/') ? workbook[..workbook.LastIndexOf('/')] : "";
            var workbookRels = (folder.Length == 0 ? "" : folder + "/") + "_rels/" + workbook[(workbook.LastIndexOf('/') + 1)..] + ".rels";
            var sheetId = FirstSheetRelationship(zip, workbook);
            var sheet = Relationships(zip, workbookRels, folder).FirstOrDefault(r => r.Id == sheetId).Target
                ?? throw new InvalidDataException("No first sheet");
            var sharedPart = Relationships(zip, workbookRels, folder).FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal)).Target;
            var shared = sharedPart is null ? [] : SharedStrings(zip, sharedPart);
            ReadSheet(zip, sheet, shared, rows);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException or InvalidOperationException
            or NotSupportedException or FormatException or ArgumentException or OverflowException)
        {
            log?.LogWarning("An uploaded spreadsheet could not be read: {Error}", ex.GetType().Name);
            throw Malformed("The spreadsheet could not be read");
        }
        return rows;
    }

    private static void ReadSheet(ZipArchive zip, string part, List<string> shared, List<List<string?>> rows)
    {
        using var reader = Open(zip, part);
        var lastRow = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row" || reader.NamespaceURI != Main)
            {
                continue;
            }
            FleetImportFile.GuardRows(rows.Count + 1);
            var number = reader.GetAttribute("r") is { } r ? int.Parse(r, System.Globalization.CultureInfo.InvariantCulture) : lastRow + 1;
            lastRow = number;
            var cells = new SortedDictionary<int, string?>();
            if (!reader.IsEmptyElement)
            {
                var depth = reader.Depth;
                var nextColumn = 0;
                while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c" && reader.NamespaceURI == Main)
                    {
                        var column = reader.GetAttribute("r") is { } reference ? ColumnOf(reference) : nextColumn;
                        nextColumn = column + 1;
                        cells[column] = Cell(reader, shared);
                    }
                }
            }
            var count = cells.Count == 0 ? 0 : Math.Min(cells.Keys.Max() + 1, FleetImportFile.MaxColumns + 1);
            var row = new List<string?>(count);
            for (var i = 0; i < count; i++)
            {
                row.Add(cells.TryGetValue(i, out var text) ? text : null);
            }
            while (rows.Count + 1 < number)
            {
                rows.Add([]); // a skipped (blank) row keeps the numbering true
                FleetImportFile.GuardRows(rows.Count);
            }
            rows.Add(row);
        }
    }

    /// <summary>One <c>&lt;c&gt;</c>: its text, the reader left on its end.</summary>
    private static string Cell(XmlReader reader, List<string> shared)
    {
        var type = reader.GetAttribute("t") ?? "n";
        if (reader.IsEmptyElement)
        {
            return "";
        }
        var depth = reader.Depth;
        string? value = null;
        var inline = new StringBuilder();
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == Main && reader.LocalName == "v")
            {
                value = reader.ReadElementContentAsString();
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == Main && reader.LocalName == "is")
            {
                inline.Append(Text(reader));
                reader.Read();
            }
            else
            {
                reader.Read();
            }
        }
        return type switch
        {
            "s" => shared[int.Parse(value ?? throw new InvalidDataException("A shared string cell without a value"),
                System.Globalization.CultureInfo.InvariantCulture)],
            "inlineStr" => inline.ToString(),
            _ => value ?? "",
        };
    }

    /// <summary>The text of a string item (<c>&lt;si&gt;</c> or <c>&lt;is&gt;</c>): its <c>&lt;t&gt;</c> runs, phonetic runs left out.</summary>
    private static string Text(XmlReader reader)
    {
        var text = new StringBuilder();
        if (reader.IsEmptyElement)
        {
            return "";
        }
        var depth = reader.Depth;
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "rPh")
            {
                reader.Skip();
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                text.Append(reader.ReadElementContentAsString());
            }
            else
            {
                reader.Read();
            }
        }
        return text.ToString();
    }

    private static List<string> SharedStrings(ZipArchive zip, string part)
    {
        var strings = new List<string>();
        using var reader = Open(zip, part);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si" && reader.NamespaceURI == Main)
            {
                strings.Add(Text(reader));
            }
        }
        return strings;
    }

    private static string FirstSheetRelationship(ZipArchive zip, string workbook)
    {
        using var reader = Open(zip, workbook);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet" && reader.NamespaceURI == Main)
            {
                return reader.GetAttribute("id", RelationshipsNs) ?? throw new InvalidDataException("A sheet without a relationship");
            }
        }
        throw new InvalidDataException("The workbook has no sheet");
    }

    private static string? Target(ZipArchive zip, string rels, string folder, string typeSuffix) =>
        Relationships(zip, rels, folder).FirstOrDefault(r => r.Type.EndsWith(typeSuffix, StringComparison.Ordinal)).Target;

    private static List<(string Id, string Type, string Target)> Relationships(ZipArchive zip, string rels, string folder)
    {
        var found = new List<(string, string, string)>();
        if (zip.GetEntry(rels) is null)
        {
            return found;
        }
        using var reader = Open(zip, rels);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" && reader.NamespaceURI == PackageRelationships)
            {
                var target = reader.GetAttribute("Target") ?? "";
                target = target.StartsWith('/') ? target[1..] : (folder.Length == 0 ? "" : folder + "/") + target;
                found.Add((reader.GetAttribute("Id") ?? "", reader.GetAttribute("Type") ?? "", target));
            }
        }
        return found;
    }

    private static XmlReader Open(ZipArchive zip, string part) =>
        XmlReader.Create((zip.GetEntry(part) ?? throw new InvalidDataException("Missing part " + part)).Open(), Safe);

    /// <summary>A1-style reference to a zero-based column index.</summary>
    private static int ColumnOf(string reference)
    {
        var column = 0;
        var letters = 0;
        foreach (var c in reference)
        {
            if (c is >= 'A' and <= 'Z')
            {
                column = checked(column * 26 + (c - 'A' + 1));
                letters++;
            }
            else
            {
                break;
            }
        }
        if (letters == 0 || letters > 3)
        {
            throw new InvalidDataException("A cell reference without a column");
        }
        return column - 1;
    }

    // ---------------------------------------------------------------------------------------------- inspection

    /// <summary>
    /// Reads every entry once, counting real inflated bytes (not the sizes the archive claims); refuses bombs, DOCTYPEs and
    /// entity declarations (see <see cref="MarkupScanner"/>). Uses the central directory, like the parser, so archives
    /// written in streaming mode (with data descriptors) are read correctly.
    /// </summary>
    public static void Inspect(byte[] bytes, ILogger? log)
    {
        long total = 0;
        var entries = 0;
        var buffer = new byte[16 * 1024];
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (++entries > FleetImportFile.MaxEntries)
                {
                    throw Unsafe();
                }
                var xml = entry.FullName.EndsWith(".xml", StringComparison.Ordinal) || entry.FullName.EndsWith(".rels", StringComparison.Ordinal);
                var scanner = xml ? new MarkupScanner() : null;
                using var input = entry.Open();
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > FleetImportFile.MaxUncompressed)
                    {
                        throw Unsafe();
                    }
                    if (scanner is not null && scanner.Feed(buffer, read))
                    {
                        throw Unsafe();
                    }
                }
            }
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or InvalidOperationException
            or ArgumentException)
        {
            log?.LogWarning("An uploaded spreadsheet archive could not be read: {Error}", ex.GetType().Name);
            throw Malformed("The spreadsheet could not be read");
        }
        if (entries == 0)
        {
            throw Malformed("The spreadsheet is empty");
        }
    }

    /// <summary>
    /// Finds a <c>&lt;!DOCTYPE</c> or <c>&lt;!ENTITY</c> anywhere in one XML part, across read boundaries and in any letter
    /// case, and refuses parts not encoded in an ASCII-compatible encoding (UTF-16 or UTF-32, recognised by their byte-order
    /// mark or a zero byte in the first four), which would hide the markup from a byte scan (Java <c>MarkupScanner</c>).
    /// </summary>
    internal sealed class MarkupScanner
    {
        private static readonly byte[][] Forbidden = [Encoding.ASCII.GetBytes("<!DOCTYPE"), Encoding.ASCII.GetBytes("<!ENTITY")];
        private const int Carry = 8; // one byte shorter than the longest marker

        private byte[] carry = [];
        private long seen;

        /// <summary>True when the part must be refused.</summary>
        public bool Feed(byte[] buffer, int length)
        {
            if (seen == 0 && NotAsciiCompatible(buffer, length))
            {
                return true;
            }
            seen += length;
            var window = new byte[carry.Length + length];
            carry.CopyTo(window, 0);
            Array.Copy(buffer, 0, window, carry.Length, length);
            if (Forbidden.Any(marker => Contains(window, marker)))
            {
                return true;
            }
            var keep = Math.Min(Carry, window.Length);
            carry = window[^keep..];
            return false;
        }

        private static bool NotAsciiCompatible(byte[] b, int length)
        {
            if (length >= 2 && ((b[0] == 0xFE && b[1] == 0xFF) || (b[0] == 0xFF && b[1] == 0xFE)))
            {
                return true; // UTF-16 (or UTF-32LE) byte-order mark
            }
            for (var i = 0; i < Math.Min(4, length); i++)
            {
                if (b[i] == 0)
                {
                    return true; // UTF-16 or UTF-32 without a mark
                }
            }
            return false;
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length && match; j++)
                {
                    var c = haystack[i + j];
                    if (c is >= (byte)'a' and <= (byte)'z')
                    {
                        c -= 32;
                    }
                    match = c == needle[j];
                }
                if (match)
                {
                    return true;
                }
            }
            return false;
        }
    }

    private static ApiException Unsafe() => new(400, "IMPORT_FILE_UNSAFE",
        "The spreadsheet is not a plain workbook (too large once unpacked, or with embedded definitions)", "file");

    private static ApiException Malformed(string message) => new(400, "IMPORT_FILE_MALFORMED", message, "file");

    // ------------------------------------------------------------------------------------------------- writing

    /// <summary>One sheet to write: its name, rows of text, column widths, and whether the first row is bold.</summary>
    public sealed record Sheet(string Name, IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<int> Widths, bool BoldHeader);

    /// <summary>
    /// A plain workbook (what <c>fastexcel</c> wrote for the template): text cells as shared strings, one bold style, column
    /// widths. Application name "ZAWED", version 1.0.
    /// </summary>
    public static byte[] Write(IReadOnlyList<Sheet> sheets)
    {
        var shared = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        int Shared(string text)
        {
            if (!index.TryGetValue(text, out var i))
            {
                i = shared.Count;
                shared.Add(text);
                index[text] = i;
            }
            return i;
        }

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var sheetParts = new List<string>();
            for (var s = 0; s < sheets.Count; s++)
            {
                var sheet = sheets[s];
                sheetParts.Add(Part(zip, $"xl/worksheets/sheet{s + 1}.xml", w =>
                {
                    w.WriteStartElement("worksheet", Main);
                    w.WriteAttributeString("xmlns", "r", null, RelationshipsNs);
                    if (sheet.Widths.Count > 0)
                    {
                        w.WriteStartElement("cols", Main);
                        for (var c = 0; c < sheet.Widths.Count; c++)
                        {
                            w.WriteStartElement("col", Main);
                            w.WriteAttributeString("min", (c + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                            w.WriteAttributeString("max", (c + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                            w.WriteAttributeString("width", sheet.Widths[c].ToString(System.Globalization.CultureInfo.InvariantCulture));
                            w.WriteAttributeString("customWidth", "true");
                            w.WriteEndElement();
                        }
                        w.WriteEndElement();
                    }
                    w.WriteStartElement("sheetData", Main);
                    for (var r = 0; r < sheet.Rows.Count; r++)
                    {
                        w.WriteStartElement("row", Main);
                        w.WriteAttributeString("r", (r + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        for (var c = 0; c < sheet.Rows[r].Count; c++)
                        {
                            w.WriteStartElement("c", Main);
                            w.WriteAttributeString("r", ColumnName(c) + (r + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                            if (r == 0 && sheet.BoldHeader)
                            {
                                w.WriteAttributeString("s", "1");
                            }
                            w.WriteAttributeString("t", "s");
                            w.WriteElementString("v", Main, Shared(sheet.Rows[r][c]).ToString(System.Globalization.CultureInfo.InvariantCulture));
                            w.WriteEndElement();
                        }
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                    w.WriteEndElement();
                }));
            }
            Part(zip, "xl/sharedStrings.xml", w =>
            {
                w.WriteStartElement("sst", Main);
                w.WriteAttributeString("count", shared.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteAttributeString("uniqueCount", shared.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var text in shared)
                {
                    w.WriteStartElement("si", Main);
                    w.WriteStartElement("t", Main);
                    w.WriteAttributeString("xml", "space", null, "preserve");
                    w.WriteString(text);
                    w.WriteEndElement();
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            });
            Part(zip, "xl/styles.xml", w =>
            {
                w.WriteStartElement("styleSheet", Main);
                w.WriteStartElement("fonts", Main);
                w.WriteAttributeString("count", "2");
                w.WriteStartElement("font", Main);
                w.WriteStartElement("sz", Main);
                w.WriteAttributeString("val", "11");
                w.WriteEndElement();
                w.WriteStartElement("name", Main);
                w.WriteAttributeString("val", "Calibri");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteStartElement("font", Main);
                w.WriteStartElement("b", Main);
                w.WriteEndElement();
                w.WriteStartElement("sz", Main);
                w.WriteAttributeString("val", "11");
                w.WriteEndElement();
                w.WriteStartElement("name", Main);
                w.WriteAttributeString("val", "Calibri");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteRaw("<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
                    + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
                    + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                    + "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
                    + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"true\"/></cellXfs>");
                w.WriteEndElement();
            });
            Part(zip, "xl/workbook.xml", w =>
            {
                w.WriteStartElement("workbook", Main);
                w.WriteAttributeString("xmlns", "r", null, RelationshipsNs);
                w.WriteStartElement("sheets", Main);
                for (var s = 0; s < sheets.Count; s++)
                {
                    w.WriteStartElement("sheet", Main);
                    w.WriteAttributeString("name", sheets[s].Name);
                    w.WriteAttributeString("sheetId", (s + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteAttributeString("r", "id", RelationshipsNs, $"rId{s + 3}");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteEndElement();
            });
            Part(zip, "xl/_rels/workbook.xml.rels", w =>
            {
                w.WriteStartElement("Relationships", PackageRelationships);
                Relationship(w, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
                Relationship(w, "rId2", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings", "sharedStrings.xml");
                for (var s = 0; s < sheets.Count; s++)
                {
                    Relationship(w, $"rId{s + 3}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet",
                        $"worksheets/sheet{s + 1}.xml");
                }
                w.WriteEndElement();
            });
            Part(zip, "docProps/app.xml", w =>
            {
                const string extended = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
                w.WriteStartElement("Properties", extended);
                w.WriteElementString("Application", extended, "ZAWED");
                w.WriteElementString("AppVersion", extended, "1.0000");
                w.WriteEndElement();
            });
            Part(zip, "_rels/.rels", w =>
            {
                w.WriteStartElement("Relationships", PackageRelationships);
                Relationship(w, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
                Relationship(w, "rId2", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties", "docProps/app.xml");
                w.WriteEndElement();
            });
            Part(zip, "[Content_Types].xml", w =>
            {
                const string types = "http://schemas.openxmlformats.org/package/2006/content-types";
                w.WriteStartElement("Types", types);
                void Default(string extension, string type)
                {
                    w.WriteStartElement("Default", types);
                    w.WriteAttributeString("Extension", extension);
                    w.WriteAttributeString("ContentType", type);
                    w.WriteEndElement();
                }
                void Override(string part, string type)
                {
                    w.WriteStartElement("Override", types);
                    w.WriteAttributeString("PartName", "/" + part);
                    w.WriteAttributeString("ContentType", type);
                    w.WriteEndElement();
                }
                Default("rels", "application/vnd.openxmlformats-package.relationships+xml");
                Default("xml", "application/xml");
                Override("xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                Override("xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
                Override("xl/sharedStrings.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml");
                foreach (var part in sheetParts)
                {
                    Override(part, "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                }
                Override("docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml");
                w.WriteEndElement();
            });
        }
        return output.ToArray();
    }

    private static string Part(ZipArchive zip, string name, Action<XmlWriter> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        writer.WriteStartDocument(standalone: true);
        write(writer);
        writer.WriteEndDocument();
        return name;
    }

    private static void Relationship(XmlWriter w, string id, string type, string target)
    {
        w.WriteStartElement("Relationship", PackageRelationships);
        w.WriteAttributeString("Id", id);
        w.WriteAttributeString("Type", type);
        w.WriteAttributeString("Target", target);
        w.WriteEndElement();
    }

    private static string ColumnName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26)
        {
            name = (char)('A' + (index - 1) % 26) + name;
        }
        return name;
    }
}
