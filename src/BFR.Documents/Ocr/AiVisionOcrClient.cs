namespace BFR.Documents.Ocr;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using BFR.Documents.Abstractions;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

public enum AiOcrPreset
{
    UnlimitedOcr2Sglang,
    OllamaLocal,
    OpenAiCompatible
}

public sealed record AiOcrConfig(
    AiOcrPreset Preset,
    string BaseUrl,
    string ModelName,
    string? ApiKey = null,
    int WindowSize = 4,
    int RenderDpi = 150);

public sealed record AiOcrRunResult(
    string Markdown,
    string RawOutput,
    int PagesProcessed,
    IReadOnlyList<OcrCropRegion> CropRegions,
    IReadOnlyList<byte[]> RenderedPagePngs);

/// <summary>
/// Opt-in AI Vision OCR client supporting local SGLang (Unlimited-OCR-2), local Ollama VLMs,
/// and OpenAI-compatible vision chat-completions endpoints with sliding-window PDF page batching.
/// </summary>
public sealed class AiVisionOcrClient
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private readonly HttpClient _http;

    public AiVisionOcrClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? SharedHttpClient;
    }

    public static AiOcrConfig DefaultFor(AiOcrPreset preset) => preset switch
    {
        AiOcrPreset.UnlimitedOcr2Sglang => new AiOcrConfig(
            AiOcrPreset.UnlimitedOcr2Sglang,
            "http://127.0.0.1:10000",
            "Unlimited-OCR-2"),
        AiOcrPreset.OllamaLocal => new AiOcrConfig(
            AiOcrPreset.OllamaLocal,
            "http://127.0.0.1:11434",
            "qwen2.5vl"),
        AiOcrPreset.OpenAiCompatible => new AiOcrConfig(
            AiOcrPreset.OpenAiCompatible,
            "https://api.openai.com",
            "gpt-4o-mini"),
        _ => new AiOcrConfig(
            AiOcrPreset.UnlimitedOcr2Sglang,
            "http://127.0.0.1:10000",
            "Unlimited-OCR-2")
    };

    /// <summary>
    /// Probes <c>/v1/models</c> (or <c>/api/tags</c> for Ollama) with an 8-second timeout.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync(AiOcrConfig config, CancellationToken ct = default)
    {
        if (config is null || string.IsNullOrWhiteSpace(config.BaseUrl))
            return (false, "Base URL is required.");

        if (!Uri.TryCreate(config.BaseUrl.Trim(), UriKind.Absolute, out var parsedUri) ||
            (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            return (false, "Base URL must be a valid http:// or https:// address.");
        }

        var probeUrl = BuildProbeUrl(config.BaseUrl, config.Preset);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, probeUrl);
            ApplyAuthHeader(req, config.ApiKey);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                return (true, $"Connected ({(int)resp.StatusCode} {resp.ReasonPhrase}) — {config.ModelName}");
            }

            return (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from {probeUrl}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, $"Connection timed out after 8s ({probeUrl}).");
        }
        catch (OperationCanceledException)
        {
            return (false, "Connection test cancelled.");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Renders PDF pages (via <c>Windows.Data.Pdf</c>) or loads an image as PNG bytes, sends
    /// sliding-window batches to <c>POST {BaseUrl}/v1/chat/completions</c>, and converts the
    /// output via <see cref="UnlimitedOcrParser.Convert"/>.
    /// </summary>
    public async Task<DocumentResult<AiOcrRunResult>> RunAsync(
        string inputPath,
        AiOcrConfig config,
        bool structuredLayout = true,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            return DocumentResult<AiOcrRunResult>.Fail(DocumentErrorCode.FileNotFound, $"File not found: {Path.GetFileName(inputPath)}");

        if (config is null || string.IsNullOrWhiteSpace(config.BaseUrl))
            return DocumentResult<AiOcrRunResult>.Fail(DocumentErrorCode.Unknown, "AI OCR Base URL is not configured.");

        if (!Uri.TryCreate(config.BaseUrl.Trim(), UriKind.Absolute, out var parsedUri) ||
            (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            return DocumentResult<AiOcrRunResult>.Fail(DocumentErrorCode.Unknown, "AI OCR Base URL must be a valid http:// or https:// address.");
        }

        if (string.IsNullOrWhiteSpace(config.ModelName))
            return DocumentResult<AiOcrRunResult>.Fail(DocumentErrorCode.Unknown, "AI OCR model name is required.");

        string? snapshot = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var readable = DocumentSafety.EnsureReadablePath(inputPath, out snapshot, out var usedSnapshot);

            var ext = Path.GetExtension(readable).ToLowerInvariant();
            var dpi = Math.Clamp(config.RenderDpi, 72, 300);

            IReadOnlyList<byte[]> renderedPages = ext == ".pdf"
                ? await RenderPdfPagesToPngAsync(readable, dpi, ct).ConfigureAwait(false)
                : new[] { await LoadImageAsPngBytesAsync(readable, ct).ConfigureAwait(false) };

            if (renderedPages.Count == 0)
                return DocumentResult<AiOcrRunResult>.Fail(DocumentErrorCode.Unknown, "No pages could be rendered from the input document.");

            var windowSize = Math.Clamp(config.WindowSize, 1, 16);
            var completionsUrl = BuildChatCompletionsUrl(config.BaseUrl);
            var prompt = BuildPrompt(config.Preset, structuredLayout);

            var rawSb = new StringBuilder();
            var totalPages = renderedPages.Count;

            for (var offset = 0; offset < totalPages; offset += windowSize)
            {
                ct.ThrowIfCancellationRequested();
                var batchCount = Math.Min(windowSize, totalPages - offset);
                var batch = new List<byte[]>(batchCount);
                for (var j = 0; j < batchCount; j++)
                    batch.Add(renderedPages[offset + j]);

                var batchResponse = await SendVisionBatchAsync(
                    completionsUrl,
                    config.ModelName,
                    config.ApiKey,
                    prompt,
                    batch,
                    ct).ConfigureAwait(false);

                if (rawSb.Length > 0 && batchResponse.Length > 0)
                    rawSb.Append("\n\n");
                rawSb.Append(batchResponse);

                progress?.Report((double)(offset + batchCount) / totalPages);
            }

            var rawOutput = rawSb.ToString().Trim();
            var parsed = UnlimitedOcrParser.Convert(rawOutput);

            return DocumentResult<AiOcrRunResult>.Ok(
                new AiOcrRunResult(
                    parsed.Markdown,
                    rawOutput,
                    totalPages,
                    parsed.CropRegions,
                    renderedPages),
                usedSnapshotCopy: usedSnapshot);
        }
        catch (OperationCanceledException)
        {
            return DocumentResult<AiOcrRunResult>.Fail(DocumentErrorCode.Cancelled, "AI OCR cancelled.");
        }
        catch (IOException ex) when (DocumentSafety.IsDiskFull(ex))
        {
            return DocumentResult<AiOcrRunResult>.Fail(DocumentErrorCode.DiskFull, "Insufficient disk space.");
        }
        catch (IOException ex) when (DocumentSafety.IsSharingViolation(ex))
        {
            return DocumentResult<AiOcrRunResult>.Fail(DocumentErrorCode.FileLockedByExternalApp, "File is locked by another application.");
        }
        catch (HttpRequestException ex)
        {
            return DocumentResult<AiOcrRunResult>.Fail(DocumentErrorCode.Unknown, $"AI OCR request failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return DocumentResult<AiOcrRunResult>.Fail(DocumentErrorCode.Unknown, $"AI OCR failed: {ex.Message}");
        }
        finally
        {
            DocumentSafety.CleanupTempFile(snapshot);
        }
    }

    private async Task<string> SendVisionBatchAsync(
        string completionsUrl,
        string modelName,
        string? apiKey,
        string prompt,
        IReadOnlyList<byte[]> pngBatch,
        CancellationToken ct)
    {
        var contentParts = new List<object>(pngBatch.Count + 1);
        foreach (var png in pngBatch)
        {
            var base64 = Convert.ToBase64String(png);
            contentParts.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:image/png;base64,{base64}" }
            });
        }

        contentParts.Add(new { type = "text", text = prompt });

        var payload = new
        {
            model = modelName,
            temperature = 0.0,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = contentParts
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, completionsUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyAuthHeader(req, apiKey);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var snippet = respBody.Length > 300 ? respBody[..300] + "..." : respBody;
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {snippet}");
        }

        return ExtractAssistantContent(respBody);
    }

    private static string ExtractAssistantContent(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var first = choices[0];
        if (!first.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(textProp.GetString());
                }
            }
            return sb.ToString();
        }

        return string.Empty;
    }

    private static async Task<IReadOnlyList<byte[]>> RenderPdfPagesToPngAsync(string pdfPath, int dpi, CancellationToken ct)
    {
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath, ct).ConfigureAwait(false);
        using var winrtStream = new InMemoryRandomAccessStream();
        await winrtStream.WriteAsync(pdfBytes.AsBuffer()).AsTask(ct).ConfigureAwait(false);
        winrtStream.Seek(0);

        var winrtPdf = await PdfDocument.LoadFromStreamAsync(winrtStream).AsTask(ct).ConfigureAwait(false);
        var pageCount = (int)winrtPdf.PageCount;
        var pages = new List<byte[]>(pageCount);

        var dpiScale = dpi / 96.0;
        const double maxDim = 2400.0;

        for (var i = 0; i < pageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = winrtPdf.GetPage((uint)i);
            using var renderStream = new InMemoryRandomAccessStream();

            var destW = Math.Max(64.0, Math.Round(page.Size.Width * dpiScale));
            var destH = Math.Max(64.0, Math.Round(page.Size.Height * dpiScale));
            if (destW > maxDim || destH > maxDim)
            {
                var clamp = maxDim / Math.Max(destW, destH);
                destW = Math.Max(64.0, Math.Floor(destW * clamp));
                destH = Math.Max(64.0, Math.Floor(destH * clamp));
            }

            var renderOpts = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)destW,
                DestinationHeight = (uint)destH
            };

            await page.RenderToStreamAsync(renderStream, renderOpts).AsTask(ct).ConfigureAwait(false);
            renderStream.Seek(0);

            var pngBytes = new byte[renderStream.Size];
            await renderStream.ReadAsync(pngBytes.AsBuffer(), (uint)pngBytes.Length, InputStreamOptions.None)
                .AsTask(ct).ConfigureAwait(false);
            pages.Add(pngBytes);
        }

        return pages;
    }

    private static async Task<byte[]> LoadImageAsPngBytesAsync(string imagePath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);

        // If already a valid PNG signature, return as-is
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return bytes;
        }

        using var inStream = new InMemoryRandomAccessStream();
        await inStream.WriteAsync(bytes.AsBuffer()).AsTask(ct).ConfigureAwait(false);
        inStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(inStream).AsTask(ct).ConfigureAwait(false);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask(ct).ConfigureAwait(false);

        using var pngStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, pngStream).AsTask(ct).ConfigureAwait(false);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
        pngStream.Seek(0);

        var pngBytes = new byte[pngStream.Size];
        await pngStream.ReadAsync(pngBytes.AsBuffer(), (uint)pngBytes.Length, InputStreamOptions.None)
            .AsTask(ct).ConfigureAwait(false);
        return pngBytes;
    }

    private static string BuildProbeUrl(string baseUrl, AiOcrPreset preset)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (preset == AiOcrPreset.OllamaLocal)
        {
            if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[..^3];
            return $"{trimmed}/api/tags";
        }

        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{trimmed}/models"
            : $"{trimmed}/v1/models";
    }

    private static string BuildChatCompletionsUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{trimmed}/chat/completions"
            : $"{trimmed}/v1/chat/completions";
    }

    private static string BuildPrompt(AiOcrPreset preset, bool structuredLayout)
    {
        if (preset == AiOcrPreset.UnlimitedOcr2Sglang)
        {
            return structuredLayout
                ? "Parse the document pages in reading order with full structure (<PAGE>, headings, paragraphs, tables, and figure bounding boxes)."
                : "Extract all text from the document pages accurately in reading order.";
        }

        return structuredLayout
            ? "Extract all text from these document page images as clean Markdown. Preserve headings (#/##/###), Markdown tables (| ... |), and merge paragraphs split across pages. Output only the extracted Markdown without commentary."
            : "Extract all text from these document page images accurately in reading order. Output only the extracted text without commentary.";
    }

    private static void ApplyAuthHeader(HttpRequestMessage req, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }
}
