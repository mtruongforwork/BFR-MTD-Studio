namespace BFR.Documents.Office;

using System.Text;
using System.Text.Json;
using BFR.Core.Reports;
using BFR.Documents.Abstractions;
using ClosedXML.Excel;

/// <summary>
/// Offline spreadsheet conversion: XLSX ↔ CSV and XLSX → JSON.
/// All CSV output is hardened against CSV Formula Injection via <see cref="ReportExporter.EscapeCsv"/>.
/// </summary>
public sealed class SpreadsheetConverter
{
    public Task<DocumentResult> XlsxToCsvAsync(
        string xlsxPath,
        string outputCsvPath,
        string? sheetName = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default) =>
        Task.Run(() => XlsxToCsv(xlsxPath, outputCsvPath, sheetName, recordJournal, journalDir, ct), ct);

    public DocumentResult XlsxToCsv(
        string xlsxPath,
        string outputCsvPath,
        string? sheetName = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        var safety = OpenXmlCleaner.ValidateOpenXmlArchiveSafety(xlsxPath, ct);
        if (!safety.Success) return safety;

        if (!DocumentSafety.TryPrepareOutputPath(outputCsvPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        string? snapshot = null;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        var inputBytes = DocumentSafety.SafeFileLength(xlsxPath);

        try
        {
            var readable = DocumentSafety.EnsureReadablePath(xlsxPath, out snapshot, out var usedSnapshot);
            using var wb = new XLWorkbook(readable);
            var ws = string.IsNullOrWhiteSpace(sheetName)
                ? wb.Worksheets.FirstOrDefault()
                : wb.Worksheets.FirstOrDefault(s => s.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase)) ?? wb.Worksheets.FirstOrDefault();

            if (ws is null)
                return DocumentResult.Fail(DocumentErrorCode.Unknown, "Workbook contains no worksheets.");

            var range = ws.RangeUsed();
            var sb = new StringBuilder();
            if (range is not null)
            {
                var colCount = range.ColumnCount();
                foreach (var row in range.Rows())
                {
                    ct.ThrowIfCancellationRequested();
                    var cells = new string[colCount];
                    for (var c = 1; c <= colCount; c++)
                    {
                        cells[c - 1] = ReportExporter.EscapeCsv(row.Cell(c).GetFormattedString());
                    }
                    sb.AppendLine(string.Join(',', cells));
                }
            }

            File.WriteAllText(tempOut, sb.ToString(), new UTF8Encoding(false));
            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, recordJournal, journalDir);
            tempOut = string.Empty;

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: inputBytes,
                outputBytes: DocumentSafety.SafeFileLength(finalOut),
                usedSnapshotCopy: usedSnapshot,
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "XLSX to CSV cancelled.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.Unknown, $"XLSX to CSV failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    public Task<DocumentResult> CsvToXlsxAsync(
        string csvPath,
        string outputXlsxPath,
        string sheetName = "Sheet1",
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default) =>
        Task.Run(() => CsvToXlsx(csvPath, outputXlsxPath, sheetName, recordJournal, journalDir, ct), ct);

    public DocumentResult CsvToXlsx(
        string csvPath,
        string outputXlsxPath,
        string sheetName = "Sheet1",
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(csvPath))
            return DocumentResult.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(csvPath)}");

        if (!DocumentSafety.TryPrepareOutputPath(outputXlsxPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        string? snapshot = null;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        var inputBytes = DocumentSafety.SafeFileLength(csvPath);

        try
        {
            var readable = DocumentSafety.EnsureReadablePath(csvPath, out snapshot, out var usedSnapshot);
            var text = File.ReadAllText(readable, Encoding.UTF8);
            var rows = ParseCsv(text);

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add(string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName);

            for (var r = 0; r < rows.Count; r++)
            {
                ct.ThrowIfCancellationRequested();
                var row = rows[r];
                for (var c = 0; c < row.Count; c++)
                {
                    ws.Cell(r + 1, c + 1).Value = row[c];
                }
            }

            if (rows.Count > 0)
            {
                ws.Row(1).Style.Font.Bold = true;
                ws.Columns().AdjustToContents(1, 50);
            }

            using (var outFs = File.Create(tempOut))
            {
                wb.SaveAs(outFs);
            }
            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, recordJournal, journalDir);
            tempOut = string.Empty;

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: inputBytes,
                outputBytes: DocumentSafety.SafeFileLength(finalOut),
                usedSnapshotCopy: usedSnapshot,
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "CSV to XLSX cancelled.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.Unknown, $"CSV to XLSX failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    public Task<DocumentResult> XlsxToJsonAsync(
        string xlsxPath,
        string outputJsonPath,
        string? sheetName = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default) =>
        Task.Run(() => XlsxToJson(xlsxPath, outputJsonPath, sheetName, recordJournal, journalDir, ct), ct);

    public DocumentResult XlsxToJson(
        string xlsxPath,
        string outputJsonPath,
        string? sheetName = null,
        bool recordJournal = false,
        string? journalDir = null,
        CancellationToken ct = default)
    {
        var safety = OpenXmlCleaner.ValidateOpenXmlArchiveSafety(xlsxPath, ct);
        if (!safety.Success) return safety;

        if (!DocumentSafety.TryPrepareOutputPath(outputJsonPath, allowOverwrite: false, out var finalOut, out var code, out var msg))
            return DocumentResult.Fail(code, msg!);

        string? snapshot = null;
        var tempOut = DocumentSafety.CreateTempPath(finalOut);
        var inputBytes = DocumentSafety.SafeFileLength(xlsxPath);

        try
        {
            var readable = DocumentSafety.EnsureReadablePath(xlsxPath, out snapshot, out var usedSnapshot);
            using var wb = new XLWorkbook(readable);
            var ws = string.IsNullOrWhiteSpace(sheetName)
                ? wb.Worksheets.FirstOrDefault()
                : wb.Worksheets.FirstOrDefault(s => s.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase)) ?? wb.Worksheets.FirstOrDefault();

            if (ws is null)
                return DocumentResult.Fail(DocumentErrorCode.Unknown, "Workbook contains no worksheets.");

            var records = new List<Dictionary<string, string>>();
            var range = ws.RangeUsed();
            if (range is not null)
            {
                var rows = range.Rows().ToList();
                if (rows.Count > 0)
                {
                    var colCount = range.ColumnCount();
                    var headers = new string[colCount];
                    for (var c = 1; c <= colCount; c++)
                    {
                        var h = rows[0].Cell(c).GetFormattedString().Trim();
                        headers[c - 1] = string.IsNullOrEmpty(h) ? $"Column{c}" : h;
                    }

                    for (var r = 1; r < rows.Count; r++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                        for (var c = 1; c <= colCount; c++)
                        {
                            dict[headers[c - 1]] = rows[r].Cell(c).GetFormattedString();
                        }
                        records.Add(dict);
                    }
                }
            }

            var json = JsonSerializer.Serialize(records, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            File.WriteAllText(tempOut, json, new UTF8Encoding(false));
            var journal = DocumentSafety.CommitTempFile(tempOut, finalOut, recordJournal, journalDir);
            tempOut = string.Empty;

            return DocumentResult.Ok(
                outputPath: finalOut,
                inputBytes: inputBytes,
                outputBytes: DocumentSafety.SafeFileLength(finalOut),
                usedSnapshotCopy: usedSnapshot,
                journalPath: journal);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult.Fail(DocumentErrorCode.Cancelled, "XLSX to JSON cancelled.");
        }
        catch (Exception ex)
        {
            return DocumentResult.Fail(DocumentErrorCode.Unknown, $"XLSX to JSON failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(tempOut);
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    internal static List<List<string>> ParseCsv(string content)
    {
        var rows = new List<List<string>>();
        var currentRow = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
            }
            else
            {
                if (ch == '"')
                {
                    inQuotes = true;
                }
                else if (ch == ',')
                {
                    currentRow.Add(field.ToString());
                    field.Clear();
                }
                else if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                    currentRow.Add(field.ToString());
                    field.Clear();
                    if (currentRow.Count > 1 || currentRow[0].Length > 0)
                        rows.Add(currentRow);
                    currentRow = new List<string>();
                }
                else
                {
                    field.Append(ch);
                }
            }
        }

        if (field.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(field.ToString());
            rows.Add(currentRow);
        }

        return rows;
    }
}
