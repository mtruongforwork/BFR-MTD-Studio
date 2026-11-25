namespace BFR.Core.Ai;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

public enum LocalModelKind
{
    BackgroundRemovalOnnx,
    WhisperGgml
}

public sealed record LocalModelSpec(
    string Id,
    string DisplayName,
    string FileName,
    string DownloadUrl,
    long ApproximateBytes,
    LocalModelKind Kind,
    bool RequiresPro,
    string? ExpectedSha256 = null);

public sealed record ModelDownloadProgress(long BytesDownloaded, long? TotalBytes)
{
    public double Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesDownloaded / TotalBytes.Value, 0, 1) : 0;
}

public static class LocalModelCatalog
{
    public static readonly LocalModelSpec RembgU2NetP = new(
        Id: "u2netp",
        DisplayName: "U2Net-P (Fast · ~4.7 MB)",
        FileName: "u2netp.onnx",
        DownloadUrl: "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx",
        ApproximateBytes: 4_700_000,
        Kind: LocalModelKind.BackgroundRemovalOnnx,
        RequiresPro: false);

    public static readonly LocalModelSpec RembgIsNetHd = new(
        Id: "isnet-general-use",
        DisplayName: "ISNet General HD (Pro · ~170 MB)",
        FileName: "isnet-general-use.onnx",
        DownloadUrl: "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx",
        ApproximateBytes: 178_000_000,
        Kind: LocalModelKind.BackgroundRemovalOnnx,
        RequiresPro: true);

    public static readonly LocalModelSpec WhisperTiny = new(
        Id: "whisper-tiny",
        DisplayName: "Whisper Tiny (Fast · ~75 MB)",
        FileName: "ggml-tiny.bin",
        DownloadUrl: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin",
        ApproximateBytes: 77_700_000,
        Kind: LocalModelKind.WhisperGgml,
        RequiresPro: false);

    public static readonly LocalModelSpec WhisperBase = new(
        Id: "whisper-base",
        DisplayName: "Whisper Base (Pro · ~142 MB)",
        FileName: "ggml-base.bin",
        DownloadUrl: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
        ApproximateBytes: 147_900_000,
        Kind: LocalModelKind.WhisperGgml,
        RequiresPro: true);

    public static readonly LocalModelSpec WhisperSmall = new(
        Id: "whisper-small",
        DisplayName: "Whisper Small HD (Pro · ~466 MB)",
        FileName: "ggml-small.bin",
        DownloadUrl: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
        ApproximateBytes: 488_000_000,
        Kind: LocalModelKind.WhisperGgml,
        RequiresPro: true);

    public static IReadOnlyList<LocalModelSpec> All { get; } = new[]
    {
        RembgU2NetP,
        RembgIsNetHd,
        WhisperTiny,
        WhisperBase,
        WhisperSmall
    };
}

/// <summary>
/// On-Demand Model Manager for Thin MSIX packaging:
/// downloads AI models (ONNX / Whisper GGML) once to %LocalAppData%\FileFlowStudio\Models\,
/// supports progress, cancellation, optional SHA-256 verification, and disk cleanup.
/// </summary>
public sealed class LocalModelManager
{
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromMinutes(20)
    };

    private readonly string _modelsDir;
    private readonly HttpClient _http;

    public LocalModelManager(string? modelsDirectory = null, HttpClient? httpClient = null)
    {
        _modelsDir = string.IsNullOrWhiteSpace(modelsDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileFlowStudio", "Models")
            : modelsDirectory;
        _http = httpClient ?? SharedHttp;
    }

    public string ModelsDirectory => _modelsDir;

    public string GetModelPath(LocalModelSpec spec) =>
        Path.Combine(_modelsDir, spec.FileName);

    public bool IsInstalled(LocalModelSpec spec)
    {
        var path = GetModelPath(spec);
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 1024;
        }
        catch
        {
            return false;
        }
    }

    public long GetInstalledBytes(LocalModelSpec spec)
    {
        var path = GetModelPath(spec);
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    public long GetTotalInstalledBytes()
    {
        if (!Directory.Exists(_modelsDir)) return 0;
        try
        {
            return Directory.EnumerateFiles(_modelsDir)
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }

    public bool DeleteModel(LocalModelSpec spec)
    {
        var path = GetModelPath(spec);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> EnsureDownloadedAsync(
        LocalModelSpec spec,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var finalPath = GetModelPath(spec);
        if (IsInstalled(spec))
        {
            progress?.Report(new ModelDownloadProgress(GetInstalledBytes(spec), GetInstalledBytes(spec)));
            return finalPath;
        }

        Directory.CreateDirectory(_modelsDir);
        var partPath = finalPath + "." + Guid.NewGuid().ToString("N")[..8] + ".part";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, spec.DownloadUrl);
            req.Headers.UserAgent.ParseAdd("FileFlowStudio/3.2");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength ?? spec.ApproximateBytes;
            using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, read);
                    downloaded += read;
                    progress?.Report(new ModelDownloadProgress(downloaded, totalBytes));
                }
            }

            if (!string.IsNullOrWhiteSpace(spec.ExpectedSha256))
            {
                var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(actual, spec.ExpectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA-256 mismatch for {spec.FileName}. Expected {spec.ExpectedSha256}, got {actual}.");
            }

            File.Move(partPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
            throw;
        }
    }
}
