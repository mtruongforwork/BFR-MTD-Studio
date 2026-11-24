namespace BFR.Documents.Workflow;

using BFR.Core;
using BFR.Core.Licensing;
using BFR.Documents.Abstractions;
using BFR.Documents.Office;
using BFR.Documents.Pdf;

/// <summary>
/// Thực thi các bước tài liệu của workflow / Watch Folder (v3.0) bằng các engine 100% offline của BFR.Documents.
/// <list type="bullet">
/// <item>Không bao giờ ghi đè tệp gốc: đầu ra là tệp mới cạnh tệp đầu vào (trùng tên thì tự đánh hậu tố <c>(2)</c>).</item>
/// <item>Mỗi tệp sinh ra được commit nguyên tử từ <c>.bfrtmp</c> và ghi journal <see cref="OperationKind.CreateFile"/> để hoàn tác.</item>
/// <item>Tệp không thuộc loại của bước được chuyển tiếp nguyên vẹn sang bước sau; tệp xử lý lỗi bị loại khỏi chuỗi.</item>
/// </list>
/// </summary>
public sealed class DocumentWorkflowStepExecutor : IDocumentStepExecutor
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".heif"
    };

    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".xlsx", ".pptx" };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt", ".csv", ".tsv" };

    private readonly Func<ProFeature, bool>? _allows;

    /// <param name="allows">Kiểm tra quyền Pro (thường là <c>license.Allows</c>); null = không giới hạn (đã kiểm ở tầng gọi).</param>
    public DocumentWorkflowStepExecutor(Func<ProFeature, bool>? allows = null)
    {
        _allows = allows;
    }

    public DocumentStepOutcome Execute(WorkflowStep step, IReadOnlyList<string> inputs, string journalDir, CancellationToken ct) =>
        step switch
        {
            CompressPdfStep s => PerFile(inputs, IsPdf, ProFeature.DocumentBatch, requireOnlyForBatch: true, journalDir, ct,
                (input, jd) => new PdfCompressor().Compress(input, Sibling(input, "_compressed", ".pdf"),
                    new PdfCompressionOptions(ParsePreset(s.Quality), StripMetadata: true, RecordJournal: true, JournalDir: jd), ct: ct)),

            ImagesToPdfStep s => ImagesToPdf(s, inputs, journalDir, ct),

            DocToPdfStep s => PerFile(inputs, p => OfficeExtensions.Contains(Ext(p)) || TextExtensions.Contains(Ext(p)),
                ProFeature.OfficeConversion, requireOnlyForBatch: false, journalDir, ct,
                (input, jd) => TextExtensions.Contains(Ext(input))
                    ? new TextDocumentPdfRenderer().RenderToPdf(input, Sibling(input, string.Empty, ".pdf"),
                        new TextPdfRenderOptions(RecordJournal: true, JournalDir: jd), ct)
                    : new HybridOfficePdfBridge().ConvertToPdf(input, Sibling(input, string.Empty, ".pdf"),
                        s.PreferHighFidelity, HybridOfficePdfBridge.DefaultTimeoutSeconds, recordJournal: true, jd, ct)),

            StripDocMetadataStep s => PerFile(inputs, p => IsPdf(p) || OfficeExtensions.Contains(Ext(p)),
                ProFeature.OfficeDeepClean, requireOnlyForBatch: true, journalDir, ct,
                (input, jd) => StripMetadata(s, input, jd, ct)),

            WatermarkPdfStep s => PerFile(inputs, IsPdf, ProFeature.PdfSecurityAndStamp, requireOnlyForBatch: false, journalDir, ct,
                (input, jd) => new PdfStampService().AddWatermark(input, Sibling(input, "_watermarked", ".pdf"),
                    new PdfWatermarkOptions(
                        Text: string.IsNullOrWhiteSpace(s.Text) ? "CONFIDENTIAL" : s.Text,
                        Opacity: Math.Clamp(s.Opacity, 0.05, 1.0),
                        AngleDegrees: s.Angle,
                        RecordJournal: true,
                        JournalDir: jd), ct: ct)),

            _ => DocumentStepOutcome.PassThrough(inputs)
        };

    // ------------------------------------------------------------------ từng tệp

    private DocumentStepOutcome PerFile(
        IReadOnlyList<string> inputs,
        Func<string, bool> applies,
        ProFeature feature,
        bool requireOnlyForBatch,
        string journalDir,
        CancellationToken ct,
        Func<string, string, DocumentResult> run)
    {
        var targets = inputs.Where(applies).ToList();
        if (targets.Count == 0) return DocumentStepOutcome.PassThrough(inputs);

        var outputs = new List<string>();
        var created = new List<string>();
        var journals = new List<string>();
        var errors = new List<(string, string)>();

        var needsPro = !requireOnlyForBatch || targets.Count > 1;
        var blocked = needsPro && _allows is not null && !_allows(feature);

        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            if (!applies(input))
            {
                outputs.Add(input);
                continue;
            }

            if (blocked)
            {
                errors.Add((input, FeatureMessages.For(feature)));
                continue;
            }

            DocumentResult res;
            try
            {
                res = run(input, journalDir);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                res = DocumentResult.Fail(DocumentErrorCode.Unknown, ex.Message);
            }

            if (res.Success && res.OutputPath is not null && File.Exists(res.OutputPath))
            {
                outputs.Add(res.OutputPath);
                created.Add(res.OutputPath);
                if (!string.IsNullOrEmpty(res.JournalPath)) journals.Add(res.JournalPath);
            }
            else
            {
                errors.Add((input, res.Message ?? res.ErrorCode.ToString()));
            }
        }

        return new DocumentStepOutcome(outputs, created, journals, errors);
    }

    private static DocumentResult StripMetadata(StripDocMetadataStep s, string input, string journalDir, CancellationToken ct)
    {
        if (IsPdf(input))
        {
            var pdf = new PdfSecurityService().StripMetadata(input, Sibling(input, "_clean", ".pdf"), stripUnsafeActions: true,
                password: null, recordJournal: true, journalDir, ct);
            return pdf.Success
                ? DocumentResult.Ok(pdf.OutputPath, usedSnapshotCopy: pdf.UsedSnapshotCopy)
                : DocumentResult.Fail(pdf.ErrorCode, pdf.Message ?? pdf.ErrorCode.ToString());
        }

        var office = new OpenXmlCleaner().Clean(input, Sibling(input, "_clean", Ext(input)),
            new OpenXmlCleanOptions(RemoveComments: s.RemoveComments, AcceptAndStripRevisions: s.RemoveRevisions, RecordJournal: true, JournalDir: journalDir), ct);
        return office.Success
            ? DocumentResult.Ok(office.OutputPath, usedSnapshotCopy: office.UsedSnapshotCopy)
            : DocumentResult.Fail(office.ErrorCode, office.Message ?? office.ErrorCode.ToString());
    }

    // ------------------------------------------------------------------ gộp ảnh

    private DocumentStepOutcome ImagesToPdf(ImagesToPdfStep s, IReadOnlyList<string> inputs, string journalDir, CancellationToken ct)
    {
        var images = inputs.Where(p => ImageExtensions.Contains(Ext(p))).ToList();
        if (images.Count == 0) return DocumentStepOutcome.PassThrough(inputs);

        var others = inputs.Where(p => !ImageExtensions.Contains(Ext(p))).ToList();

        if (images.Count > LicenseService.MaxFreeImageToPdfCount && _allows is not null && !_allows(ProFeature.DocumentBatch))
        {
            var msg = FeatureMessages.For(ProFeature.DocumentBatch);
            return new DocumentStepOutcome(others, Array.Empty<string>(), Array.Empty<string>(), images.Select(i => (i, msg)).ToList());
        }

        var mode = (s.PageSize ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "letter" => PdfPageSizeMode.Letter,
            "fit" or "fitimage" or "original" => PdfPageSizeMode.FitImage,
            _ => PdfPageSizeMode.A4
        };

        var dir = Path.GetDirectoryName(images[0]) ?? ".";
        var output = Path.Combine(dir, SafePdfName(s.OutputName));

        DocumentResult res;
        try
        {
            res = new PdfImageConverter().ImagesToPdf(images, output,
                new ImagesToPdfOptions(mode, 24.0, RecordJournal: true, JournalDir: journalDir), ct: ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            res = DocumentResult.Fail(DocumentErrorCode.Unknown, ex.Message);
        }

        if (!res.Success || res.OutputPath is null || !File.Exists(res.OutputPath))
        {
            var msg = res.Message ?? res.ErrorCode.ToString();
            return new DocumentStepOutcome(others, Array.Empty<string>(), Array.Empty<string>(), images.Select(i => (i, msg)).ToList());
        }

        // Các ảnh đã gộp được thay bằng MỘT tệp PDF; tệp không phải ảnh đi tiếp nguyên vẹn.
        var outputs = new List<string>(others) { res.OutputPath };
        var journals = string.IsNullOrEmpty(res.JournalPath) ? Array.Empty<string>() : new[] { res.JournalPath };
        return new DocumentStepOutcome(outputs, new[] { res.OutputPath }, journals, Array.Empty<(string, string)>());
    }

    // ------------------------------------------------------------------ tiện ích

    private static PdfCompressionPreset ParsePreset(string? quality) => (quality ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "light" or "high" => PdfCompressionPreset.Light,
        "max" or "maximum" => PdfCompressionPreset.Max,
        _ => PdfCompressionPreset.Balanced
    };

    /// <summary>Tên tệp PDF an toàn từ cấu hình workflow (JSON không đáng tin): chỉ lấy tên, bỏ ký tự cấm, luôn đuôi .pdf.</summary>
    public static string SafePdfName(string? name)
    {
        var raw = Path.GetFileName((name ?? string.Empty).Trim());
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(raw.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        if (cleaned.Length == 0) cleaned = "combined";
        if (cleaned.Length > 120) cleaned = cleaned[..120];
        return cleaned.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? cleaned : cleaned + ".pdf";
    }

    private static string Sibling(string input, string suffix, string ext) =>
        Path.Combine(Path.GetDirectoryName(input) ?? ".", Path.GetFileNameWithoutExtension(input) + suffix + ext);

    private static string Ext(string path) => Path.GetExtension(path);

    private static bool IsPdf(string path) => string.Equals(Ext(path), ".pdf", StringComparison.OrdinalIgnoreCase);
}
