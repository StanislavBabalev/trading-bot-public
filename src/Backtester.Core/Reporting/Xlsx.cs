using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Backtester.Core.Reporting;

/// <summary>
/// Minimal dependency-free .xlsx writer: inline strings, numbers, formulas with cached values, a fixed set of styles,
/// column widths, frozen panes and an autofilter. Enough for result exports; no shared strings, charts or hyperlinks.
/// </summary>
public static class Xlsx
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // OOXML namespace identifiers (names only, never fetched).
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string TypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    /// <summary>Order matches the cellXfs list in <see cref="Styles"/>.</summary>
    public enum Style { Default, Header, Title, Bold, Wrap, Number2, Integer, Percent, Money, General }

    /// <summary>
    /// A cell: a string, a number (percentages as fractions, shown with <see cref="Style.Percent"/>), or empty when null
    /// or NaN. <paramref name="Formula"/> (no leading '=') is written with <paramref name="Value"/> as its cached result.
    /// </summary>
    public readonly record struct Cell(object? Value, Style Style = Style.Default, string? Formula = null)
    {
        public static implicit operator Cell(string? s) => new(s);
    }

    public sealed class Sheet(string name)
    {
        public string Name { get; } = name;
        public List<Cell[]> Rows { get; } = [];
        /// <summary>Column widths in characters, by 0-based column.</summary>
        public Dictionary<int, double> Widths { get; } = [];
        public int FreezeRows { get; init; }
        public int FreezeCols { get; init; }
        /// <summary>0-based header row of an autofilter over the rest of the sheet; null for none.</summary>
        public int? FilterRow { get; init; }

        public Sheet Add(params Cell[] row)
        {
            Rows.Add(row);
            return this;
        }
    }

    public static string Column(int index)
    {
        var sb = new StringBuilder();
        for (int n = index + 1; n > 0; n = (n - 1) / 26) sb.Insert(0, (char)('A' + (n - 1) % 26));
        return sb.ToString();
    }

    /// <summary>A cross-sheet reference prefix such as <c>'Top trials'!</c>.</summary>
    public static string SheetRef(string sheet) => $"'{sheet.Replace("'", "''")}'!";

    public static void Write(string path, IReadOnlyList<Sheet> sheets)
    {
        if (sheets.Count == 0) throw new ArgumentException("A workbook needs at least one sheet.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sheets)
            if (s.Name.Length is 0 or > 31 || s.Name.IndexOfAny(['[', ']', ':', '*', '?', '/', '\\']) >= 0 || !names.Add(s.Name))
                throw new ArgumentException($"Invalid or duplicate sheet name '{s.Name}'.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        using (var file = File.Create(tmp))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            void Part(string name, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
                w.Write(xml);
            }

            var types = new StringBuilder($"<Types xmlns=\"{TypesNs}\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (int i = 0; i < sheets.Count; i++)
                types.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            Part("[Content_Types].xml", types.Append("</Types>").ToString());

            Part("_rels/.rels", $"<Relationships xmlns=\"{PkgRelNs}\"><Relationship Id=\"rId1\" Type=\"{RelNs}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");

            var wb = new StringBuilder($"<workbook xmlns=\"{MainNs}\" xmlns:r=\"{RelNs}\"><bookViews><workbookView/></bookViews><sheets>");
            var rels = new StringBuilder($"<Relationships xmlns=\"{PkgRelNs}\">");
            var filters = new StringBuilder();
            for (int i = 0; i < sheets.Count; i++)
            {
                wb.Append($"<sheet name=\"{Esc(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"{RelNs}/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
                if (FilterRange(sheets[i]) is { } range)
                    filters.Append($"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">{Esc(SheetRef(sheets[i].Name) + range.Absolute)}</definedName>");
            }
            wb.Append("</sheets>");
            if (filters.Length > 0) wb.Append("<definedNames>").Append(filters).Append("</definedNames>");
            Part("xl/workbook.xml", wb.Append("<calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/></workbook>").ToString());
            Part("xl/_rels/workbook.xml.rels", rels.Append($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"{RelNs}/styles\" Target=\"styles.xml\"/></Relationships>").ToString());
            Part("xl/styles.xml", Styles);
            for (int i = 0; i < sheets.Count; i++) Part($"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i]));
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static (string Plain, string Absolute)? FilterRange(Sheet s)
    {
        if (s.FilterRow is not int r || r >= s.Rows.Count) return null;
        int cols = s.Rows[r].Length;
        if (cols == 0) return null;
        string last = Column(cols - 1);
        return ($"A{r + 1}:{last}{s.Rows.Count}", $"$A${r + 1}:${last}${s.Rows.Count}");
    }

    private static string SheetXml(Sheet s)
    {
        var sb = new StringBuilder($"<worksheet xmlns=\"{MainNs}\"><sheetViews><sheetView workbookViewId=\"0\">");
        if (s.FreezeRows > 0 || s.FreezeCols > 0)
        {
            string pane = s.FreezeRows > 0 && s.FreezeCols > 0 ? "bottomRight" : s.FreezeRows > 0 ? "bottomLeft" : "topRight";
            string topLeft = $"{Column(s.FreezeCols)}{s.FreezeRows + 1}";
            sb.Append("<pane");
            if (s.FreezeCols > 0) sb.Append($" xSplit=\"{s.FreezeCols}\"");
            if (s.FreezeRows > 0) sb.Append($" ySplit=\"{s.FreezeRows}\"");
            sb.Append($" topLeftCell=\"{topLeft}\" activePane=\"{pane}\" state=\"frozen\"/><selection pane=\"{pane}\" activeCell=\"{topLeft}\" sqref=\"{topLeft}\"/>");
        }
        sb.Append("</sheetView></sheetViews><sheetFormatPr defaultRowHeight=\"13.2\"/>");
        if (s.Widths.Count > 0)
        {
            sb.Append("<cols>");
            foreach (var (col, width) in s.Widths.OrderBy(kv => kv.Key))
                sb.Append($"<col min=\"{col + 1}\" max=\"{col + 1}\" width=\"{width.ToString("0.##", Inv)}\" customWidth=\"1\"/>");
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        for (int r = 0; r < s.Rows.Count; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            var row = s.Rows[r];
            for (int c = 0; c < row.Length; c++) AppendCell(sb, $"{Column(c)}{r + 1}", row[c]);
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        if (FilterRange(s) is { } range) sb.Append($"<autoFilter ref=\"{range.Plain}\"/>");
        return sb.Append("<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/></worksheet>").ToString();
    }

    private static void AppendCell(StringBuilder sb, string reference, Cell cell)
    {
        int style = (int)cell.Style;
        string s = style == 0 ? "" : $" s=\"{style}\"";
        double? number = cell.Value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            _ => null,
        };
        if (cell.Formula is { } formula)
        {
            string f = $"<f>{Esc(formula)}</f>";
            if (number is double v && double.IsFinite(v)) sb.Append($"<c r=\"{reference}\"{s}>{f}<v>{v.ToString("R", Inv)}</v></c>");
            else if (cell.Value is string str) sb.Append($"<c r=\"{reference}\"{s} t=\"str\">{f}<v>{Esc(Clip(str))}</v></c>");
            else sb.Append($"<c r=\"{reference}\"{s}>{f}</c>");
            return;
        }
        if (number is double x)
        {
            if (double.IsFinite(x)) sb.Append($"<c r=\"{reference}\"{s}><v>{x.ToString("R", Inv)}</v></c>");
            else if (!double.IsNaN(x)) AppendText(sb, reference, s, x > 0 ? "∞" : "−∞");   // e.g. a profit factor with no losing trade
            else if (style != 0) sb.Append($"<c r=\"{reference}\"{s}/>");
            return;
        }
        string? text = cell.Value switch
        {
            null => null,
            bool b => b ? "yes" : "no",
            IFormattable fm => fm.ToString(null, Inv),
            _ => cell.Value.ToString(),
        };
        if (string.IsNullOrEmpty(text))
        {
            if (style != 0) sb.Append($"<c r=\"{reference}\"{s}/>");
            return;
        }
        AppendText(sb, reference, s, text);
    }

    private static void AppendText(StringBuilder sb, string reference, string style, string text) =>
        sb.Append($"<c r=\"{reference}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(Clip(text))}</t></is></c>");

    /// <summary>Excel's cell limit is 32,767 characters.</summary>
    private static string Clip(string s) => s.Length <= 32_000 ? s : s[..32_000] + "…";

    /// <summary>XML-escapes and drops characters XML 1.0 cannot carry.</summary>
    private static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\t' or '\n' or '\r': sb.Append(ch); break;
                case < ' ': break;
                case '￾' or '￿': break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private const string Font = "<sz val=\"10\"/><name val=\"Arial\"/><family val=\"2\"/>";

    // cellXfs order must match Style: Default, Header, Title, Bold, Wrap, Number2, Integer, Percent, Money, General.
    private static readonly string Styles =
        $"<styleSheet xmlns=\"{MainNs}\">" +
        "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"$#,##0.00;-$#,##0.00\"/></numFmts>" +
        $"<fonts count=\"3\"><font>{Font}</font><font><b/>{Font}</font><font><b/><sz val=\"14\"/><name val=\"Arial\"/><family val=\"2\"/></font></fonts>" +
        "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE8E8E4\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
        "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
        "<border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FF9A9A9A\"/></bottom><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"10\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>" +
        "<xf numFmtId=\"2\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" +
        "<xf numFmtId=\"3\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" +
        "<xf numFmtId=\"10\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" +
        "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"top\"/></xf>" +
        "</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";
}
