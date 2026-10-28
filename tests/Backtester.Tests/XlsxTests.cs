using System.IO.Compression;
using System.Xml.Linq;
using Backtester.Core.Reporting;
using static Backtester.Core.Reporting.Xlsx;

namespace Backtester.Tests;

public class XlsxTests : IDisposable
{
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bt_xlsx_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Column_letters_follow_excel()
    {
        Assert.Equal(["A", "Z", "AA", "AZ", "BA", "ZZ", "AAA"], new[] { 0, 25, 26, 51, 52, 701, 702 }.Select(Column));
    }

    [Fact]
    public void Workbook_parts_are_well_formed_and_cells_round_trip()
    {
        var data = new Sheet("Data") { FreezeRows = 1, FreezeCols = 1, FilterRow = 0 };
        data.Widths[0] = 20;
        data.Add(new Cell("Name", Style.Header), new Cell("Return", Style.Header));
        data.Add("a <b> & \"c\"\u0001", new Cell(0.25, Style.Percent));
        data.Add("no losses", new Cell(double.PositiveInfinity, Style.Number2));
        data.Add("missing", new Cell(double.NaN, Style.Number2));
        var summary = new Sheet("Bob's summary")
            .Add(new Cell(0.25, Style.Percent, $"MAX({SheetRef("Data")}$B$2:$B$4)"), new Cell("a", Style.Default, $"INDEX({SheetRef("Data")}$A$2:$A$4,1)"));
        string path = Path.Combine(_dir, "t.xlsx");
        Xlsx.Write(path, [summary, data]);

        using var zip = ZipFile.OpenRead(path);
        var parts = zip.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); return XDocument.Load(s); });   // throws if malformed
        Assert.Equal(["[Content_Types].xml", "_rels/.rels", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml"],
                     parts.Keys.Order(StringComparer.Ordinal));

        var wb = parts["xl/workbook.xml"].Root!;
        Assert.Equal(["Bob's summary", "Data"], wb.Descendants(M + "sheet").Select(s => (string)s.Attribute("name")!));
        var filter = Assert.Single(wb.Descendants(M + "definedName"));
        Assert.Equal(("_xlnm._FilterDatabase", "1", "'Data'!$A$1:$B$4"), ((string)filter.Attribute("name")!, (string)filter.Attribute("localSheetId")!, filter.Value));

        XElement Cell(string sheet, string r) => parts[sheet].Descendants(M + "c").Single(c => (string)c.Attribute("r")! == r);
        string Text(XElement c) => c.Element(M + "is")!.Element(M + "t")!.Value;
        const string D = "xl/worksheets/sheet2.xml", S = "xl/worksheets/sheet1.xml";
        Assert.Equal("a <b> & \"c\"", Text(Cell(D, "A2")));                       // escaped, control character dropped
        Assert.Equal(("0.25", "7"), (Cell(D, "B2").Element(M + "v")!.Value, (string)Cell(D, "B2").Attribute("s")!));
        Assert.Equal("∞", Text(Cell(D, "B3")));
        Assert.Empty(Cell(D, "B4").Elements());                                  // NaN: styled but empty
        var pane = parts[D].Descendants(M + "pane").Single();
        Assert.Equal(("1", "1", "B2", "frozen"), ((string)pane.Attribute("xSplit")!, (string)pane.Attribute("ySplit")!, (string)pane.Attribute("topLeftCell")!, (string)pane.Attribute("state")!));
        Assert.Equal("A1:B4", (string)parts[D].Descendants(M + "autoFilter").Single().Attribute("ref")!);
        Assert.Equal(20, (double)parts[D].Descendants(M + "col").Single().Attribute("width")!);

        Assert.Equal(("MAX('Data'!$B$2:$B$4)", "0.25"), (Cell(S, "A1").Element(M + "f")!.Value, Cell(S, "A1").Element(M + "v")!.Value));
        Assert.Equal(("str", "a"), ((string)Cell(S, "B1").Attribute("t")!, Cell(S, "B1").Element(M + "v")!.Value));
        Assert.Equal("1", (string)parts["xl/workbook.xml"].Descendants(M + "calcPr").Single().Attribute("fullCalcOnLoad")!);
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData("")]
    [InlineData("a name that is longer than thirty-one characters")]
    public void Invalid_sheet_names_are_refused(string name)
    {
        Assert.Throws<ArgumentException>(() => Xlsx.Write(Path.Combine(_dir, "x.xlsx"), [new Sheet(name).Add("x")]));
    }

    [Fact]
    public void Duplicate_sheet_names_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Xlsx.Write(Path.Combine(_dir, "x.xlsx"), [new Sheet("A").Add("x"), new Sheet("a").Add("y")]));
    }
}
