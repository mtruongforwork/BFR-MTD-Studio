namespace BFR.Documents.Tests;

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BFR.Documents.Abstractions;
using BFR.Documents.Office;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using Xunit;

/// <summary>
/// Hợp đồng chuyển đổi bảng tính và trích xuất OpenXml (v3.0). Trọng tâm là KHÔNG ÂM THẦM làm sai dữ liệu:
/// số âm, cột trùng tiêu đề, sheet không tồn tại, công thức trong CSV, số 0 đứng đầu.
/// </summary>
public sealed class SpreadsheetAndOpenXmlContractTests : IDisposable
{
    private readonly string _dir;
    private readonly SpreadsheetConverter _conv = new();

    public SpreadsheetAndOpenXmlContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr_sheet_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private string Xlsx(string name, Action<IXLWorksheet> fill, string sheet = "Data")
    {
        using var wb = new XLWorkbook();
        fill(wb.Worksheets.Add(sheet));
        wb.SaveAs(P(name));
        return P(name);
    }

    private static string[] Lines(string path) =>
        File.ReadAllText(path, Encoding.UTF8).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Where(l => l.Length > 0).ToArray();

    private void AssertNoTempFiles() => Assert.Empty(Directory.GetFiles(_dir, "*.bfrtmp", SearchOption.AllDirectories));

    // ------------------------------------------------------------------ XLSX → CSV

    [Fact]
    public void XlsxToCsv_WritesRowsAndColumnsAsDisplayed()
    {
        var x = Xlsx("a.xlsx", ws =>
        {
            ws.Cell("A1").Value = "Name"; ws.Cell("B1").Value = "Qty";
            ws.Cell("A2").Value = "Apple"; ws.Cell("B2").Value = 3;
            ws.Cell("A3").Value = "Pear"; ws.Cell("B3").Value = 12;
        });

        var res = _conv.XlsxToCsv(x, P("a.csv"));

        Assert.True(res.Success, res.Message);
        Assert.Equal(new[] { "Name,Qty", "Apple,3", "Pear,12" }, Lines(P("a.csv")));
    }

    [Fact]
    public void XlsxToCsv_QuotesCommasQuotesAndLineBreaks_SoTheyRoundTrip()
    {
        var x = Xlsx("q.xlsx", ws =>
        {
            ws.Cell("A1").Value = "a,b";
            ws.Cell("B1").Value = "say \"hi\"";
            ws.Cell("C1").Value = "line1\nline2";
        });

        Assert.True(_conv.XlsxToCsv(x, P("q.csv")).Success);
        var rows = SpreadsheetConverter.ParseCsv(File.ReadAllText(P("q.csv")));

        var row = Assert.Single(rows);
        Assert.Equal(new[] { "a,b", "say \"hi\"", "line1\nline2" }, row);
    }

    [Fact]
    public void XlsxToCsv_NegativeNumbers_ArePreservedExactly()
    {
        var x = Xlsx("neg.xlsx", ws =>
        {
            ws.Cell("A1").Value = "Amount";
            ws.Cell("A2").Value = -5.5;
            ws.Cell("A3").Value = -100;
        });

        Assert.True(_conv.XlsxToCsv(x, P("neg.csv")).Success);

        Assert.Equal(new[] { "Amount", "-5.5", "-100" }, Lines(P("neg.csv")));   // không được thành '-5.5
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")")]
    [InlineData("@SUM(A1)")]
    [InlineData("+cmd")]
    [InlineData("-cmd")]
    public void XlsxToCsv_TextThatLooksLikeAFormula_IsNeutralised(string text)
    {
        var x = Xlsx("f.xlsx", ws => ws.Cell("A1").SetValue(text));

        Assert.True(_conv.XlsxToCsv(x, P("f.csv")).Success);

        var written = File.ReadAllText(P("f.csv")).TrimEnd('\r', '\n');
        Assert.StartsWith("'", written.TrimStart('"'));    // tiền tố ' vô hiệu hoá công thức khi mở bằng Excel
    }

    [Fact]
    public void XlsxToCsv_PicksTheRequestedSheet_CaseInsensitively()
    {
        using (var wb = new XLWorkbook())
        {
            wb.Worksheets.Add("First").Cell("A1").Value = "one";
            wb.Worksheets.Add("Second").Cell("A1").Value = "two";
            wb.SaveAs(P("two.xlsx"));
        }

        Assert.True(_conv.XlsxToCsv(P("two.xlsx"), P("def.csv")).Success);
        Assert.True(_conv.XlsxToCsv(P("two.xlsx"), P("sel.csv"), "SECOND").Success);

        Assert.Equal(new[] { "one" }, Lines(P("def.csv")));
        Assert.Equal(new[] { "two" }, Lines(P("sel.csv")));
    }

    [Fact]
    public void XlsxToCsv_MissingSheet_IsAnError_NotASilentSubstitute()
    {
        var x = Xlsx("one.xlsx", ws => ws.Cell("A1").Value = "only");

        var res = _conv.XlsxToCsv(x, P("out.csv"), sheetName: "DoesNotExist");

        Assert.False(res.Success);
        Assert.False(File.Exists(P("out.csv")));      // không xuất nhầm dữ liệu của sheet khác
        AssertNoTempFiles();
    }

    [Fact]
    public void XlsxToCsv_FormattedDatesAndPercentages_AreWrittenAsDisplayed()
    {
        var x = Xlsx("fmt.xlsx", ws =>
        {
            ws.Cell("A1").Value = new DateTime(2024, 3, 5);
            ws.Cell("A1").Style.DateFormat.Format = "yyyy-MM-dd";
            ws.Cell("B1").Value = 0.25;
            ws.Cell("B1").Style.NumberFormat.Format = "0%";
        });

        Assert.True(_conv.XlsxToCsv(x, P("fmt.csv")).Success);

        Assert.Equal(new[] { "2024-03-05,25%" }, Lines(P("fmt.csv")));    // hiển thị theo định dạng ô
    }

    [Fact]
    public void XlsxToCsv_PreservesUnicode_WithoutByteOrderMark()
    {
        var x = Xlsx("u.xlsx", ws => { ws.Cell("A1").Value = "Tiếng Việt"; ws.Cell("B1").Value = "中文"; });

        Assert.True(_conv.XlsxToCsv(x, P("u.csv")).Success);

        var bytes = File.ReadAllBytes(P("u.csv"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Contains("Tiếng Việt,中文", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void XlsxToCsv_EmptySheet_ProducesAnEmptyCsv()
    {
        var x = Xlsx("empty.xlsx", _ => { });

        var res = _conv.XlsxToCsv(x, P("empty.csv"));

        Assert.True(res.Success, res.Message);
        Assert.Equal(0, new FileInfo(P("empty.csv")).Length);
    }

    [Fact]
    public void XlsxToCsv_MissingInput_IsReportedAsFileNotFound()
    {
        var res = _conv.XlsxToCsv(P("ghost.xlsx"), P("out.csv"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.FileNotFound, res.ErrorCode);
    }

    [Fact]
    public void XlsxToCsv_NotARealWorkbook_FailsCleanly()
    {
        File.WriteAllText(P("fake.xlsx"), "this is not a zip");

        var res = _conv.XlsxToCsv(P("fake.xlsx"), P("out.csv"));

        Assert.False(res.Success);
        Assert.False(File.Exists(P("out.csv")));
        AssertNoTempFiles();
    }

    [Fact]
    public void XlsxToCsv_ExistingOutput_IsNeverOverwritten()
    {
        var x = Xlsx("a.xlsx", ws => ws.Cell("A1").Value = "x");
        File.WriteAllText(P("out.csv"), "precious");

        var res = _conv.XlsxToCsv(x, P("out.csv"));

        Assert.True(res.Success, res.Message);
        Assert.Equal("precious", File.ReadAllText(P("out.csv")));
        Assert.Equal("out (2).csv", Path.GetFileName(res.OutputPath));
    }

    // ------------------------------------------------------------------ XLSX → JSON

    [Fact]
    public void XlsxToJson_UsesTheFirstRowAsKeys()
    {
        var x = Xlsx("j.xlsx", ws =>
        {
            ws.Cell("A1").Value = "Name"; ws.Cell("B1").Value = "City";
            ws.Cell("A2").Value = "An"; ws.Cell("B2").Value = "Hà Nội";
            ws.Cell("A3").Value = "Binh"; ws.Cell("B3").Value = "Huế";
        });

        var res = _conv.XlsxToJson(x, P("j.json"));

        Assert.True(res.Success, res.Message);
        var json = File.ReadAllText(P("j.json"));
        Assert.Contains("Hà Nội", json);                     // không bị escape thành à
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("Binh", doc.RootElement[1].GetProperty("Name").GetString());
        Assert.Equal("Huế", doc.RootElement[1].GetProperty("City").GetString());
    }

    [Fact]
    public void XlsxToJson_EmptyHeaderCell_GetsAPositionalName()
    {
        var x = Xlsx("j.xlsx", ws =>
        {
            ws.Cell("A1").Value = "Name"; ws.Cell("C1").Value = "Note";
            ws.Cell("A2").Value = "An"; ws.Cell("B2").Value = "middle"; ws.Cell("C2").Value = "hi";
        });

        Assert.True(_conv.XlsxToJson(x, P("j.json")).Success);

        using var doc = JsonDocument.Parse(File.ReadAllText(P("j.json")));
        Assert.Equal("middle", doc.RootElement[0].GetProperty("Column2").GetString());
    }

    [Fact]
    public void XlsxToJson_DuplicateHeaders_DoNotLoseAnyColumn()
    {
        var x = Xlsx("dup.xlsx", ws =>
        {
            ws.Cell("A1").Value = "Name"; ws.Cell("B1").Value = "Name"; ws.Cell("C1").Value = "Name";
            ws.Cell("A2").Value = "first"; ws.Cell("B2").Value = "second"; ws.Cell("C2").Value = "third";
        });

        Assert.True(_conv.XlsxToJson(x, P("dup.json")).Success);

        var json = File.ReadAllText(P("dup.json"));
        Assert.Contains("first", json);
        Assert.Contains("second", json);
        Assert.Contains("third", json);
    }

    [Fact]
    public void XlsxToJson_OnlyHeaderRow_IsAnEmptyArray()
    {
        var x = Xlsx("h.xlsx", ws => { ws.Cell("A1").Value = "Name"; ws.Cell("B1").Value = "City"; });

        Assert.True(_conv.XlsxToJson(x, P("h.json")).Success);

        using var doc = JsonDocument.Parse(File.ReadAllText(P("h.json")));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void XlsxToJson_MissingSheet_IsAnError()
    {
        var x = Xlsx("one.xlsx", ws => ws.Cell("A1").Value = "only");

        var res = _conv.XlsxToJson(x, P("o.json"), sheetName: "Nope");

        Assert.False(res.Success);
        Assert.False(File.Exists(P("o.json")));
    }

    // ------------------------------------------------------------------ CSV → XLSX

    private string Csv(string name, string content, Encoding? enc = null)
    {
        File.WriteAllText(P(name), content, enc ?? new UTF8Encoding(false));
        return P(name);
    }

    private static string[][] Grid(string xlsx)
    {
        using var wb = new XLWorkbook(xlsx);
        var ws = wb.Worksheets.First();
        var range = ws.RangeUsed();
        if (range is null) return Array.Empty<string[]>();
        return range.Rows().Select(r => Enumerable.Range(1, range.ColumnCount()).Select(c => r.Cell(c).GetString()).ToArray()).ToArray();
    }

    [Fact]
    public void CsvToXlsx_QuotedCommasQuotesAndNewlines_ArePreserved()
    {
        var csv = Csv("q.csv", "a,b,c\r\n\"x,y\",\"say \"\"hi\"\"\",\"l1\nl2\"\r\n");

        var res = _conv.CsvToXlsx(csv, P("q.xlsx"));

        Assert.True(res.Success, res.Message);
        var grid = Grid(P("q.xlsx"));
        Assert.Equal(new[] { "a", "b", "c" }, grid[0]);
        Assert.Equal(new[] { "x,y", "say \"hi\"", "l1\nl2" }, grid[1]);
    }

    [Fact]
    public void CsvToXlsx_FormulaLikeCells_StayPlainText_NeverBecomeFormulas()
    {
        var csv = Csv("f.csv", "=1+1,=HYPERLINK(\"http://evil\"),@SUM(A1)\r\n");

        Assert.True(_conv.CsvToXlsx(csv, P("f.xlsx")).Success);

        using var wb = new XLWorkbook(P("f.xlsx"));
        var ws = wb.Worksheets.First();
        foreach (var addr in new[] { "A1", "B1", "C1" })
        {
            Assert.False(ws.Cell(addr).HasFormula, $"{addr} became a formula");
            Assert.Equal(XLDataType.Text, ws.Cell(addr).DataType);
        }
        Assert.Equal("=1+1", ws.Cell("A1").GetString());
    }

    [Fact]
    public void CsvToXlsx_LeadingZerosAndLongNumbers_AreNotMangled()
    {
        var csv = Csv("z.csv", "007,0123456789012345678,1e5,00.50\r\n");

        Assert.True(_conv.CsvToXlsx(csv, P("z.xlsx")).Success);

        Assert.Equal(new[] { "007", "0123456789012345678", "1e5", "00.50" }, Grid(P("z.xlsx"))[0]);
    }

    [Theory]
    [InlineData("a,b\r\n1,2\r\n")]      // CRLF
    [InlineData("a,b\n1,2\n")]          // LF
    [InlineData("a,b\r1,2\r")]          // CR cũ
    [InlineData("a,b\n1,2")]            // không có dòng cuối
    [InlineData("a,b\n\n1,2\n\n")]      // dòng trống bị bỏ qua
    public void CsvToXlsx_AllLineEndingStyles_GiveTheSameTwoRows(string content)
    {
        var res = _conv.CsvToXlsx(Csv("e.csv", content), P("e.xlsx"));

        Assert.True(res.Success, res.Message);
        var grid = Grid(P("e.xlsx"));
        Assert.Equal(2, grid.Length);
        Assert.Equal(new[] { "a", "b" }, grid[0]);
        Assert.Equal(new[] { "1", "2" }, grid[1]);
    }

    [Fact]
    public void CsvToXlsx_ByteOrderMark_IsNotPartOfTheFirstCell()
    {
        var csv = Csv("bom.csv", "Name,City\r\nAn,Hà Nội\r\n", new UTF8Encoding(true));

        Assert.True(_conv.CsvToXlsx(csv, P("bom.xlsx")).Success);

        var grid = Grid(P("bom.xlsx"));
        Assert.Equal("Name", grid[0][0]);
        Assert.Equal("Hà Nội", grid[1][1]);
    }

    [Fact]
    public void CsvToXlsx_EmptyFile_ProducesAnEmptyWorkbook()
    {
        var res = _conv.CsvToXlsx(Csv("empty.csv", ""), P("empty.xlsx"));

        Assert.True(res.Success, res.Message);
        Assert.Empty(Grid(P("empty.xlsx")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void CsvToXlsx_BlankSheetName_FallsBackToSheet1(string name)
    {
        Assert.True(_conv.CsvToXlsx(Csv("a.csv", "x\r\n"), P("a.xlsx"), name).Success);

        using var wb = new XLWorkbook(P("a.xlsx"));
        Assert.Equal("Sheet1", wb.Worksheets.First().Name);
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData("this sheet name is much longer than thirty one characters")]
    public void CsvToXlsx_InvalidSheetName_FailsWithoutThrowing_OrLeavingTempFiles(string name)
    {
        var res = _conv.CsvToXlsx(Csv("a.csv", "x\r\n"), P("a.xlsx"), name);

        Assert.False(res.Success);
        Assert.False(File.Exists(P("a.xlsx")));
        AssertNoTempFiles();
    }

    [Fact]
    public void CsvToXlsx_MissingInput_IsReportedAsFileNotFound()
    {
        var res = _conv.CsvToXlsx(P("ghost.csv"), P("o.xlsx"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.FileNotFound, res.ErrorCode);
    }

    [Fact]
    public void CsvToXlsx_Cancelled_ReturnsCancelled_AndWritesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var res = _conv.CsvToXlsx(Csv("a.csv", "a,b\r\n1,2\r\n"), P("a.xlsx"), ct: cts.Token);

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.Cancelled, res.ErrorCode);
        Assert.False(File.Exists(P("a.xlsx")));
        AssertNoTempFiles();
    }

    // ------------------------------------------------------------------ ParseCsv

    [Theory]
    [InlineData("a,b\n1,2", 2, "a|b;1|2")]
    [InlineData("\"a,b\",c", 1, "a,b|c")]
    [InlineData("\"he said \"\"hi\"\"\"", 1, "he said \"hi\"")]
    [InlineData("a,b\r\n", 1, "a|b")]
    [InlineData("", 0, "")]
    [InlineData(",,", 1, "||")]                    // 3 ô rỗng
    [InlineData("a\n\nb", 2, "a;b")]               // dòng trống bị bỏ
    [InlineData("\"unterminated", 1, "unterminated")]   // lệch dấu nháy: không ném lỗi, giữ phần đã đọc
    [InlineData("x,\"y\nz\",w", 1, "x|y\nz|w")]    // xuống dòng trong ô có nháy
    public void ParseCsv_HandlesQuotingAndLineBreaks(string content, int rowCount, string flattened)
    {
        var rows = SpreadsheetConverter.ParseCsv(content);

        Assert.Equal(rowCount, rows.Count);
        Assert.Equal(flattened, string.Join(";", rows.Select(r => string.Join("|", r))));
    }

    // ------------------------------------------------------------------ OpenXmlExtractor

    private string FakeOffice(string name, params (string Entry, byte[] Data)[] entries)
    {
        var path = P(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, data) in entries)
        {
            var e = zip.CreateEntry(entry);
            using var s = e.Open();
            s.Write(data);
        }
        return path;
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void ExtractEmbeddedImages_OnlyTakesMediaEntriesWithAnExtension_InOrder()
    {
        var doc = FakeOffice("d.docx",
            ("[Content_Types].xml", Bytes("<Types/>")),
            ("word/document.xml", Bytes("<w/>")),
            ("word/media/image1.png", Bytes("PNG1")),
            ("word/media/noextension", Bytes("skip")),
            ("word/media/image2.jpg", Bytes("JPG2")),
            ("word/embeddings/other.bin", Bytes("skip")));

        var res = new OpenXmlExtractor().ExtractEmbeddedImages(doc, P("imgs"));

        Assert.True(res.Success, res.Message);
        Assert.Equal(new[] { "d_img001.png", "d_img002.jpg" }, res.OutputPaths.Select(Path.GetFileName).ToArray());
        Assert.Equal("PNG1", File.ReadAllText(res.OutputPaths[0]));
        Assert.Equal("JPG2", File.ReadAllText(res.OutputPaths[1]));
        AssertNoTempFiles();
    }

    [Fact]
    public void ExtractEmbeddedImages_NoMedia_IsASuccessWithNoFiles()
    {
        var doc = FakeOffice("d.docx", ("[Content_Types].xml", Bytes("<Types/>")), ("word/document.xml", Bytes("<w/>")));

        var res = new OpenXmlExtractor().ExtractEmbeddedImages(doc, P("imgs"));

        Assert.True(res.Success, res.Message);
        Assert.Empty(res.OutputPaths);
    }

    [Fact]
    public void ExtractEmbeddedImages_RunTwice_NeverOverwritesEarlierResults()
    {
        var doc = FakeOffice("d.pptx", ("ppt/media/a.png", Bytes("A")));
        var extractor = new OpenXmlExtractor();

        var first = extractor.ExtractEmbeddedImages(doc, P("imgs"));
        var second = extractor.ExtractEmbeddedImages(doc, P("imgs"));

        Assert.True(first.Success && second.Success);
        Assert.NotEqual(first.OutputPaths[0], second.OutputPaths[0]);
        Assert.True(File.Exists(first.OutputPaths[0]));
        Assert.True(File.Exists(second.OutputPaths[0]));
    }

    [Fact]
    public void ExtractEmbeddedImages_ProtectedOutputDirectory_IsRejected()
    {
        var doc = FakeOffice("d.docx", ("word/media/a.png", Bytes("A")));
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var res = new OpenXmlExtractor().ExtractEmbeddedImages(doc, Path.Combine(windows, "bfr_never"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.ProtectedPath, res.ErrorCode);
        Assert.False(Directory.Exists(Path.Combine(windows, "bfr_never")));
    }

    [Fact]
    public void ExtractEmbeddedImages_NotAZip_FailsCleanly()
    {
        File.WriteAllText(P("fake.docx"), "not a zip");

        var res = new OpenXmlExtractor().ExtractEmbeddedImages(P("fake.docx"), P("imgs"));

        Assert.False(res.Success);
        Assert.False(Directory.Exists(P("imgs")) && Directory.GetFiles(P("imgs")).Length > 0);
    }

    [Fact]
    public void MergeCsv_SkipsRepeatedHeaders_ByDefault()
    {
        var a = Csv("a.csv", "id,name\r\n1,An\r\n");
        var b = Csv("b.csv", "id,name\r\n2,Binh\r\n");
        var c = Csv("c.csv", "id,name\r\n3,Chi\r\n");

        var res = new OpenXmlExtractor().MergeCsv(new[] { a, b, c }, P("m.csv"));

        Assert.True(res.Success, res.Message);
        Assert.Equal(new[] { "id,name", "1,An", "2,Binh", "3,Chi" }, Lines(P("m.csv")));
    }

    [Fact]
    public void MergeCsv_KeepsAllHeaders_WhenAskedTo()
    {
        var a = Csv("a.csv", "id,name\r\n1,An\r\n");
        var b = Csv("b.csv", "id,name\r\n2,Binh\r\n");

        Assert.True(new OpenXmlExtractor().MergeCsv(new[] { a, b }, P("m.csv"), skipDuplicateHeaders: false).Success);

        Assert.Equal(new[] { "id,name", "1,An", "id,name", "2,Binh" }, Lines(P("m.csv")));
    }

    [Fact]
    public void MergeCsv_DifferentHeader_IsKeptAsData_NotDropped()
    {
        var a = Csv("a.csv", "id,name\r\n1,An\r\n");
        var b = Csv("b.csv", "code,label\r\n9,Z\r\n");

        Assert.True(new OpenXmlExtractor().MergeCsv(new[] { a, b }, P("m.csv")).Success);

        Assert.Contains("code,label", Lines(P("m.csv")));      // khác tiêu đề đầu → không phải trùng lặp, không được nuốt
    }

    [Fact]
    public void MergeCsv_NoInputs_IsFileNotFound()
    {
        var res = new OpenXmlExtractor().MergeCsv(Array.Empty<string>(), P("m.csv"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.FileNotFound, res.ErrorCode);
    }

    [Fact]
    public void MergeCsv_MissingFile_FailsWithoutLeavingOutputOrTempFiles()
    {
        var a = Csv("a.csv", "id\r\n1\r\n");

        var res = new OpenXmlExtractor().MergeCsv(new[] { a, P("ghost.csv") }, P("m.csv"));

        Assert.False(res.Success);
        Assert.False(File.Exists(P("m.csv")));
        AssertNoTempFiles();
    }

    [Fact]
    public void MergeDocx_KeepsAllText_AndInsertsPageBreaksBetweenDocuments()
    {
        var a = TestDocumentFixtures.CreateSampleDocx(P("a.docx"), "Alpha text");
        var b = TestDocumentFixtures.CreateSampleDocx(P("b.docx"), "Beta text");
        var c = TestDocumentFixtures.CreateSampleDocx(P("c.docx"), "Gamma text");

        var res = new OpenXmlExtractor().MergeDocx(new[] { a, b, c }, P("m.docx"));

        Assert.True(res.Success, res.Message);
        var text = new OpenXmlExtractor().ExtractText(P("m.docx")).Data!;
        Assert.Contains("Alpha text", text);
        Assert.Contains("Beta text", text);
        Assert.Contains("Gamma text", text);
        using var doc = WordprocessingDocument.Open(P("m.docx"), false);
        var breaks = doc.MainDocumentPart!.Document.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.Break>()
            .Count(x => x.Type?.Value == DocumentFormat.OpenXml.Wordprocessing.BreakValues.Page);
        Assert.Equal(2, breaks);
    }

    [Fact]
    public void MergeDocx_WithoutPageBreaks_HasNone()
    {
        var a = TestDocumentFixtures.CreateSampleDocx(P("a.docx"), "Alpha");
        var b = TestDocumentFixtures.CreateSampleDocx(P("b.docx"), "Beta");

        Assert.True(new OpenXmlExtractor().MergeDocx(new[] { a, b }, P("m.docx"), insertPageBreaks: false).Success);

        using var doc = WordprocessingDocument.Open(P("m.docx"), false);
        Assert.Empty(doc.MainDocumentPart!.Document.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.Break>());
    }

    [Fact]
    public void MergeDocx_OneBadInput_FailsBeforeWritingAnything()
    {
        var a = TestDocumentFixtures.CreateSampleDocx(P("a.docx"), "Alpha");
        File.WriteAllText(P("bad.docx"), "not a docx");

        var res = new OpenXmlExtractor().MergeDocx(new[] { a, P("bad.docx") }, P("m.docx"));

        Assert.False(res.Success);
        Assert.False(File.Exists(P("m.docx")));
        AssertNoTempFiles();
    }

    [Fact]
    public void MergeDocx_NoInputs_IsFileNotFound()
    {
        var res = new OpenXmlExtractor().MergeDocx(Array.Empty<string>(), P("m.docx"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.FileNotFound, res.ErrorCode);
    }

    [Fact]
    public void ExtractText_FromDocx_ReturnsTheBodyText()
    {
        var a = TestDocumentFixtures.CreateSampleDocx(P("a.docx"), "Xin chào thế giới");

        var res = new OpenXmlExtractor().ExtractText(a);

        Assert.True(res.Success, res.Message);
        Assert.Contains("Xin chào thế giới", res.Data);
    }

    [Fact]
    public void ExtractText_FromXlsx_IsExplicitlyUnsupported_SoCallersRouteToSpreadsheetConverter()
    {
        var x = Xlsx("t.xlsx", ws => ws.Cell("A1").Value = "Doanh thu");

        var res = new OpenXmlExtractor().ExtractText(x);

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.UnsupportedFormat, res.ErrorCode);
    }

    [Fact]
    public void ExtractText_MissingFile_IsFileNotFound()
    {
        var res = new OpenXmlExtractor().ExtractText(P("ghost.docx"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.FileNotFound, res.ErrorCode);
    }
}
