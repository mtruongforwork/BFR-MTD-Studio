namespace BFR.Cli;

using BFR.Core.Licensing;
using BFR.Documents.Abstractions;
using BFR.Documents.Ocr;
using BFR.Documents.Office;
using BFR.Documents.Pdf;

internal static class DocumentCommands
{
    public static int RunPdf(string[] args, LicenseService license, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        if (args.Length == 0)
        {
            PrintPdfHelp(stderr);
            return 1;
        }

        var sub = args[0].ToLowerInvariant();
        var rest = args[1..];

        return sub switch
        {
            "merge" => PdfMerge(rest, license, stdout, stderr, journalDir),
            "split" => PdfSplit(rest, license, stdout, stderr, journalDir),
            "compress" => PdfCompress(rest, stdout, stderr, journalDir),
            "to-img" => PdfToImages(rest, license, stdout, stderr, journalDir),
            "img-to-pdf" => ImagesToPdf(rest, license, stdout, stderr, journalDir),
            "ocr" => PdfOcr(rest, license, stdout, stderr, journalDir),
            "protect" => PdfProtect(rest, license, stdout, stderr, journalDir),
            "unlock" => PdfUnlock(rest, license, stdout, stderr, journalDir),
            "watermark" => PdfWatermark(rest, license, stdout, stderr, journalDir),
            _ => UnknownPdf(sub, stderr)
        };
    }

    public static int RunDoc(string[] args, LicenseService license, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        if (args.Length == 0)
        {
            PrintDocHelp(stderr);
            return 1;
        }

        var sub = args[0].ToLowerInvariant();
        var rest = args[1..];

        return sub switch
        {
            "to-pdf" => DocToPdf(rest, stdout, stderr, journalDir),
            "clean-meta" => DocCleanMeta(rest, stdout, stderr, journalDir),
            "to-md" => DocToMarkdown(rest, license, stdout, stderr, journalDir),
            "xlsx-csv" => XlsxCsv(rest, stdout, stderr, journalDir),
            _ => UnknownDoc(sub, stderr)
        };
    }

    public static int RunInternalOfficeConvert(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2)
        {
            stderr.WriteLine("Usage: bfr internal-office-convert <input> <output.pdf>");
            return 1;
        }

        var bridge = new HybridOfficePdfBridge();
        var res = bridge.ConvertToPdf(args[0], args[1]);
        if (!res.Success)
        {
            stderr.WriteLine($"[{res.ErrorCode}] {res.Message}");
            return 3;
        }

        stdout.WriteLine(res.OutputPath);
        return 0;
    }

    private static int PdfMerge(string[] args, LicenseService license, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        var outPath = GetOpt(args, "--out", "-o");
        var password = GetOpt(args, "--password");
        var inputs = PositionalArgs(args);

        if (inputs.Count == 0 || string.IsNullOrWhiteSpace(outPath))
        {
            stderr.WriteLine("bfr pdf merge <files...> --out <output.pdf>");
            return 1;
        }

        var gate = license.CheckPdfMergeCount(inputs.Count);
        if (!gate.Allowed)
        {
            stderr.WriteLine(gate.Message);
            return 1;
        }

        Dictionary<string, string>? pwds = null;
        if (!string.IsNullOrEmpty(password))
        {
            pwds = inputs.ToDictionary(p => p, _ => password, StringComparer.OrdinalIgnoreCase);
        }

        var res = new PdfOrganizer().Merge(inputs, outPath, new PdfMergeOptions(RecordJournal: true, JournalDir: journalDir, Passwords: pwds));
        return PrintResult(res, stdout, stderr);
    }

    private static int PdfSplit(string[] args, LicenseService license, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        var outDir = GetOpt(args, "--out", "-o") ?? Directory.GetCurrentDirectory();
        var ranges = GetOpt(args, "--ranges");
        var chunkStr = GetOpt(args, "--chunks");
        var cutsStr = GetOpt(args, "--cuts");
        var password = GetOpt(args, "--password");
        var inputs = PositionalArgs(args);

        if (inputs.Count == 0)
        {
            stderr.WriteLine("bfr pdf split <input.pdf> [--ranges \"1-3,4-end\" | --chunks N | --cuts \"3,7\"] --out <dir>");
            return 1;
        }

        var desc = new DocumentInspector().Inspect(inputs[0], password);
        if (desc.ErrorCode == DocumentErrorCode.None || desc.ErrorCode == DocumentErrorCode.LargeDocumentWarning)
        {
            var gate = license.CheckPdfPageCount(desc.PageCount);
            if (!gate.Allowed)
            {
                stderr.WriteLine(gate.Message);
                return 1;
            }
        }

        var organizer = new PdfOrganizer();
        var opts = new PdfSplitOptions(outDir, Password: password, RecordJournal: true, JournalDir: journalDir);
        DocumentResult res;

        if (!string.IsNullOrWhiteSpace(cutsStr))
        {
            var cuts = cutsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var v) ? v : -1)
                .Where(v => v > 0)
                .ToList();
            res = organizer.SplitByCutPoints(inputs[0], cuts, opts);
        }
        else if (!string.IsNullOrWhiteSpace(chunkStr) && int.TryParse(chunkStr, out var chunkSize))
        {
            res = organizer.SplitIntoFixedChunks(inputs[0], chunkSize, opts);
        }
        else
        {
            var groups = string.IsNullOrWhiteSpace(ranges)
                ? new[] { "1-end" }
                : ranges.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            res = organizer.SplitByRanges(inputs[0], groups, opts);
        }

        return PrintResult(res, stdout, stderr);
    }

    private static int PdfCompress(string[] args, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        var outPath = GetOpt(args, "--out", "-o");
        var presetStr = GetOpt(args, "--preset") ?? "balanced";
        var password = GetOpt(args, "--password");
        var inputs = PositionalArgs(args);

        if (inputs.Count == 0)
        {
            stderr.WriteLine("bfr pdf compress <input.pdf> [--out <output.pdf>] [--preset light|balanced|max]");
            return 1;
        }

        var input = inputs[0];
        outPath ??= Path.Combine(Path.GetDirectoryName(input) ?? ".", $"{Path.GetFileNameWithoutExtension(input)}_compressed.pdf");
        var preset = presetStr.ToLowerInvariant() switch
        {
            "light" => PdfCompressionPreset.Light,
            "max" => PdfCompressionPreset.Max,
            _ => PdfCompressionPreset.Balanced
        };

        var res = new PdfCompressor().Compress(input, outPath, new PdfCompressionOptions(preset, true, password, true, journalDir));
        return PrintResult(res, stdout, stderr);
    }

    private static int PdfToImages(string[] args, LicenseService license, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        var outDir = GetOpt(args, "--out", "-o") ?? Directory.GetCurrentDirectory();
        var fmtStr = GetOpt(args, "--format-out") ?? "png";
        var dpiStr = GetOpt(args, "--dpi");
        var range = GetOpt(args, "--ranges");
        var password = GetOpt(args, "--password");
        var inputs = PositionalArgs(args);

        if (inputs.Count == 0)
        {
            stderr.WriteLine("bfr pdf to-img <input.pdf> --out <dir> [--format-out png|jpg] [--dpi 150]");
            return 1;
        }

        var desc = new DocumentInspector().Inspect(inputs[0], password);
        if (desc.ErrorCode == DocumentErrorCode.None || desc.ErrorCode == DocumentErrorCode.LargeDocumentWarning)
        {
            var gate = license.CheckImageToPdfCount(desc.PageCount);
            if (!gate.Allowed) { stderr.WriteLine(gate.Message); return 1; }
        }

        var fmt = fmtStr.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? PdfExportImageFormat.Jpg : PdfExportImageFormat.Png;
        var dpi = int.TryParse(dpiStr, out var d) ? d : 150;
        var res = new PdfImageConverter().PdfToImagesAsync(
            inputs[0],
            outDir,
            new PdfToImagesOptions(fmt, dpi, 90, range, password, true, journalDir)).GetAwaiter().GetResult();

        return PrintResult(res, stdout, stderr);
    }

    private static int ImagesToPdf(string[] args, LicenseService license, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        var outPath = GetOpt(args, "--out", "-o");
        var sizeStr = GetOpt(args, "--size") ?? "a4";
        var inputs = PositionalArgs(args);

        if (inputs.Count == 0 || string.IsNullOrWhiteSpace(outPath))
        {
            stderr.WriteLine("bfr pdf img-to-pdf <images...> --out <output.pdf> [--size a4|letter|fit]");
            return 1;
        }

        var gate = license.CheckImageToPdfCount(inputs.Count);
        if (!gate.Allowed) { stderr.WriteLine(gate.Message); return 1; }

        var size = sizeStr.ToLowerInvariant() switch
        {
            "letter" => PdfPageSizeMode.Letter,
            "fit" => PdfPageSizeMode.FitImage,
            _ => PdfPageSizeMode.A4
        };

        var res = new PdfImageConverter().ImagesToPdf(inputs, outPath, new ImagesToPdfOptions(size, 24, true, journalDir));
        return PrintResult(res, stdout, stderr);
    }

    private static int PdfOcr(string[] args, LicenseService license, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        var outPath = GetOpt(args, "--out", "-o");
        var lang = GetOpt(args, "--lang");
        var fmtStr = GetOpt(args, "--format-out") ?? "txt";
        var password = GetOpt(args, "--password");
        var inputs = PositionalArgs(args);

        if (inputs.Count == 0)
        {
            stderr.WriteLine("bfr pdf ocr <input.pdf|image> [--lang en|vi] [--format-out txt|md|pdf] [--out <file>]");
            return 1;
        }

        var fmt = fmtStr.ToLowerInvariant() switch
        {
            "md" => OcrOutputFormat.Markdown,
            "pdf" => OcrOutputFormat.SearchablePdf,
            _ => OcrOutputFormat.Text
        };

        var maxPages = license.Allows(ProFeature.OfflineOcr) ? (int?)null : LicenseService.MaxFreeOcrPages;
        var ocr = new WindowsOcrService();
        var ext = Path.GetExtension(inputs[0]).ToLowerInvariant();
        if (ext == ".pdf")
        {
            var res = ocr.RecognizePdfAsync(inputs[0], lang, outPath, fmt, maxPages, password, true, journalDir).GetAwaiter().GetResult();
            if (!res.Success)
            {
                stderr.WriteLine($"[{res.ErrorCode}] {res.Message}");
                return 3;
            }
            stdout.WriteLine(outPath is not null ? $"OCR saved: {res.OutputPath}" : res.Data?.FullText);
            return 0;
        }
        else
        {
            var res = ocr.RecognizeImageAsync(inputs[0], lang).GetAwaiter().GetResult();
            if (!res.Success)
            {
                stderr.WriteLine($"[{res.ErrorCode}] {res.Message}");
                return 3;
            }
            if (outPath is not null) File.WriteAllText(outPath, res.Data?.FullText ?? string.Empty);
            stdout.WriteLine(outPath is not null ? $"OCR saved: {outPath}" : res.Data?.FullText);
            return 0;
        }
    }

    private static int PdfProtect(string[] args, LicenseService license, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        if (!license.Allows(ProFeature.PdfSecurityAndStamp))
        {
            stderr.WriteLine(FeatureMessages.For(ProFeature.PdfSecurityAndStamp));
            return 1;
        }

        var outPath = GetOpt(args, "--out", "-o");
        var userPwd = GetOpt(args, "--user-password");
        var ownerPwd = GetOpt(args, "--owner-password");
        var existingPwd = GetOpt(args, "--password");
        var inputs = PositionalArgs(args);

        if (inputs.Count == 0 || (string.IsNullOrEmpty(userPwd) && string.IsNullOrEmpty(ownerPwd)))
        {
            stderr.WriteLine("bfr pdf protect <input.pdf> --user-password <pwd> [--owner-password <pwd>] [--out <output.pdf>]");
            return 1;
        }

        outPath ??= Path.Combine(Path.GetDirectoryName(inputs[0]) ?? ".", $"{Path.GetFileNameWithoutExtension(inputs[0])}_protected.pdf");
        var res = new PdfSecurityService().Protect(
            inputs[0],
            outPath,
            new PdfProtectOptions(userPwd, ownerPwd, ExistingPassword: existingPwd, RecordJournal: true, JournalDir: journalDir));

        return PrintResult(res, stdout, stderr);
    }

    private static int PdfUnlock(string[] args, LicenseService license, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        if (!license.Allows(ProFeature.PdfSecurityAndStamp))
        {
            stderr.WriteLine(FeatureMessages.For(ProFeature.PdfSecurityAndStamp));
            return 1;
        }

        var outPath = GetOpt(args, "--out", "-o");
        var password = GetOpt(args, "--password") ?? string.Empty;
        var inputs = PositionalArgs(args);

        if (inputs.Count == 0 || string.IsNullOrEmpty(password))
        {
            stderr.WriteLine("bfr pdf unlock <input.pdf> --password <pwd> [--out <output.pdf>]");
            return 1;
        }

        outPath ??= Path.Combine(Path.GetDirectoryName(inputs[0]) ?? ".", $"{Path.GetFileNameWithoutExtension(inputs[0])}_unlocked.pdf");
        var res = new PdfSecurityService().Unlock(inputs[0], outPath, password, true, journalDir);
        return PrintResult(res, stdout, stderr);
    }

    private static int PdfWatermark(string[] args, LicenseService license, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        if (!license.Allows(ProFeature.PdfSecurityAndStamp))
        {
            stderr.WriteLine(FeatureMessages.For(ProFeature.PdfSecurityAndStamp));
            return 1;
        }

        var outPath = GetOpt(args, "--out", "-o");
        var text = GetOpt(args, "--text") ?? "CONFIDENTIAL";
        var password = GetOpt(args, "--password");
        var inputs = PositionalArgs(args);

        if (inputs.Count == 0)
        {
            stderr.WriteLine("bfr pdf watermark <input.pdf> --text \"CONFIDENTIAL\" [--out <output.pdf>]");
            return 1;
        }

        outPath ??= Path.Combine(Path.GetDirectoryName(inputs[0]) ?? ".", $"{Path.GetFileNameWithoutExtension(inputs[0])}_watermarked.pdf");
        var res = new PdfStampService().AddWatermark(
            inputs[0],
            outPath,
            new PdfWatermarkOptions(Text: text, Password: password, RecordJournal: true, JournalDir: journalDir));

        return PrintResult(res, stdout, stderr);
    }

    private static int DocToPdf(string[] args, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        var outPath = GetOpt(args, "--out", "-o");
        var inputs = PositionalArgs(args);
        if (inputs.Count == 0)
        {
            stderr.WriteLine("bfr doc to-pdf <file.docx|xlsx|pptx|md|txt|csv> [--out <output.pdf>]");
            return 1;
        }

        outPath ??= Path.Combine(Path.GetDirectoryName(inputs[0]) ?? ".", $"{Path.GetFileNameWithoutExtension(inputs[0])}.pdf");
        var res = new HybridOfficePdfBridge().ConvertToPdf(inputs[0], outPath, recordJournal: true, journalDir: journalDir);
        return PrintResult(res, stdout, stderr);
    }

    private static int DocCleanMeta(string[] args, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        var outPath = GetOpt(args, "--out", "-o");
        var inputs = PositionalArgs(args);
        if (inputs.Count == 0)
        {
            stderr.WriteLine("bfr doc clean-meta <file.pdf|docx|xlsx|pptx> [--out <output>]");
            return 1;
        }

        var ext = Path.GetExtension(inputs[0]).ToLowerInvariant();
        outPath ??= Path.Combine(Path.GetDirectoryName(inputs[0]) ?? ".", $"{Path.GetFileNameWithoutExtension(inputs[0])}_clean{ext}");

        if (ext == ".pdf")
        {
            var res = new PdfSecurityService().StripMetadata(inputs[0], outPath, true, null, true, journalDir);
            if (!res.Success) { stderr.WriteLine($"[{res.ErrorCode}] {res.Message}"); return 3; }
            stdout.WriteLine($"Cleaned PDF -> {res.OutputPath} (fields={res.Data?.FieldsCleared}, xmp={res.Data?.RemovedXmpStream})");
            return 0;
        }
        else
        {
            var res = new OpenXmlCleaner().Clean(inputs[0], outPath, new OpenXmlCleanOptions(RecordJournal: true, JournalDir: journalDir));
            if (!res.Success) { stderr.WriteLine($"[{res.ErrorCode}] {res.Message}"); return 3; }
            stdout.WriteLine($"Cleaned Office doc -> {res.OutputPath} (itemsRemoved={res.Data?.TotalItemsRemoved})");
            return 0;
        }
    }

    private static int DocToMarkdown(string[] args, LicenseService license, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        if (!license.Allows(ProFeature.OfficeConversion))
        {
            stderr.WriteLine(FeatureMessages.For(ProFeature.OfficeConversion));
            return 1;
        }

        var outPath = GetOpt(args, "--out", "-o");
        var password = GetOpt(args, "--password");
        var inputs = PositionalArgs(args);
        if (inputs.Count == 0)
        {
            stderr.WriteLine("bfr doc to-md <input.pdf> [--out <output.md>]");
            return 1;
        }

        outPath ??= Path.Combine(Path.GetDirectoryName(inputs[0]) ?? ".", $"{Path.GetFileNameWithoutExtension(inputs[0])}.md");
        var res = new PdfExtractor().ExportToMarkdown(inputs[0], outPath, password, true, journalDir);
        return PrintResult(res, stdout, stderr);
    }

    private static int XlsxCsv(string[] args, TextWriter stdout, TextWriter stderr, string journalDir)
    {
        var outPath = GetOpt(args, "--out", "-o");
        var inputs = PositionalArgs(args);
        if (inputs.Count == 0)
        {
            stderr.WriteLine("bfr doc xlsx-csv <input.xlsx|csv> [--out <output.csv|xlsx>]");
            return 1;
        }

        var ext = Path.GetExtension(inputs[0]).ToLowerInvariant();
        var conv = new SpreadsheetConverter();
        DocumentResult res;
        if (ext == ".csv")
        {
            outPath ??= Path.Combine(Path.GetDirectoryName(inputs[0]) ?? ".", $"{Path.GetFileNameWithoutExtension(inputs[0])}.xlsx");
            res = conv.CsvToXlsx(inputs[0], outPath, recordJournal: true, journalDir: journalDir);
        }
        else
        {
            outPath ??= Path.Combine(Path.GetDirectoryName(inputs[0]) ?? ".", $"{Path.GetFileNameWithoutExtension(inputs[0])}.csv");
            res = conv.XlsxToCsv(inputs[0], outPath, recordJournal: true, journalDir: journalDir);
        }

        return PrintResult(res, stdout, stderr);
    }

    private static int PrintResult(DocumentResult res, TextWriter stdout, TextWriter stderr)
    {
        if (!res.Success)
        {
            stderr.WriteLine($"[{res.ErrorCode}] {res.Message}");
            return res.ErrorCode == DocumentErrorCode.FreeTierLimitReached ? 1 : 3;
        }

        foreach (var p in res.OutputPaths) stdout.WriteLine(p);
        return 0;
    }

    private static string? GetOpt(string[] args, params string[] names)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (names.Contains(args[i], StringComparer.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static List<string> PositionalArgs(string[] args)
    {
        var list = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                i++; // skip option value
                continue;
            }
            list.Add(args[i]);
        }
        return list;
    }

    private static int UnknownPdf(string sub, TextWriter stderr)
    {
        stderr.WriteLine($"Unknown pdf subcommand: {sub}");
        PrintPdfHelp(stderr);
        return 1;
    }

    private static int UnknownDoc(string sub, TextWriter stderr)
    {
        stderr.WriteLine($"Unknown doc subcommand: {sub}");
        PrintDocHelp(stderr);
        return 1;
    }

    private static void PrintPdfHelp(TextWriter stderr) =>
        stderr.WriteLine("bfr pdf <merge|split|compress|to-img|img-to-pdf|ocr|protect|unlock|watermark> ...");

    private static void PrintDocHelp(TextWriter stderr) =>
        stderr.WriteLine("bfr doc <to-pdf|clean-meta|to-md|xlsx-csv> ...");
}
