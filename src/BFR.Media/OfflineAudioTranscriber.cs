namespace BFR.Media;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Whisper.net;

public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

public sealed record TranscriptionResult(
    string InputPath,
    string Language,
    TimeSpan ProcessedDuration,
    bool WasTruncatedByFreeLimit,
    IReadOnlyList<TranscriptSegment> Segments,
    string PlainText,
    string SrtText,
    string VttText);

/// <summary>
/// Offline Audio/Video speech-to-text engine powered by Whisper.net (MIT) + Windows Media Foundation (NAudio, MIT).
/// Decodes MP4, MP3, M4A, WAV, WMA, AAC, MOV natively on Windows without shipping FFmpeg.
/// </summary>
public sealed class OfflineAudioTranscriber
{
    public static readonly TimeSpan FreeTierMaxDuration = TimeSpan.FromSeconds(60);

    public static bool IsSupportedMediaExtension(string? ext) =>
        ext?.ToLowerInvariant() is ".wav" or ".mp3" or ".m4a" or ".mp4" or ".wma" or ".aac" or ".mov" or ".mkv" or ".webm" or ".flac";

    public async Task<TranscriptionResult> TranscribeAsync(
        string mediaPath,
        string ggmlModelPath,
        string language = "auto",
        TimeSpan? maxDuration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            throw new FileNotFoundException("Media file not found.", mediaPath);
        if (string.IsNullOrWhiteSpace(ggmlModelPath) || !File.Exists(ggmlModelPath))
            throw new FileNotFoundException("Whisper GGML model not found.", ggmlModelPath);

        ct.ThrowIfCancellationRequested();

        // 1. Decode & resample any audio/video to 16kHz 16-bit mono WAV via Windows Media Foundation
        var (wavBytes, processedDuration, truncated) = await Task.Run(
            () => DecodeTo16KhzMonoWav(mediaPath, maxDuration, ct), ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.15);

        // 2. Run Whisper.net offline inference
        var segments = new List<TranscriptSegment>();
        using var factory = WhisperFactory.FromPath(ggmlModelPath);
        var builder = factory.CreateBuilder()
            .WithThreads(Math.Max(1, Environment.ProcessorCount / 2));

        if (!string.IsNullOrWhiteSpace(language) && !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            builder = builder.WithLanguage(language);
        else
            builder = builder.WithLanguage("auto");

        await using var processor = builder.Build();
        using var wavStream = new MemoryStream(wavBytes);

        var totalSeconds = Math.Max(1.0, processedDuration.TotalSeconds);
        await foreach (var seg in processor.ProcessAsync(wavStream, ct).ConfigureAwait(false))
        {
            var text = seg.Text?.Trim() ?? string.Empty;
            if (text.Length > 0)
            {
                segments.Add(new TranscriptSegment(seg.Start, seg.End, text));
            }
            var frac = 0.15 + 0.85 * Math.Clamp(seg.End.TotalSeconds / totalSeconds, 0, 1);
            progress?.Report(frac);
        }

        progress?.Report(1.0);

        return new TranscriptionResult(
            InputPath: mediaPath,
            Language: language,
            ProcessedDuration: processedDuration,
            WasTruncatedByFreeLimit: truncated,
            Segments: segments,
            PlainText: FormatPlainText(segments),
            SrtText: FormatSrt(segments),
            VttText: FormatVtt(segments));
    }

    public static string FormatPlainText(IReadOnlyList<TranscriptSegment> segments)
    {
        if (segments.Count == 0) return string.Empty;
        return string.Join(Environment.NewLine, segments.Select(s => s.Text.Trim()).Where(t => t.Length > 0));
    }

    public static string FormatSrt(IReadOnlyList<TranscriptSegment> segments)
    {
        if (segments.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            sb.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"{FormatSrtTime(s.Start)} --> {FormatSrtTime(s.End)}");
            sb.AppendLine(s.Text.Trim());
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string FormatVtt(IReadOnlyList<TranscriptSegment> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            sb.AppendLine($"{FormatVttTime(s.Start)} --> {FormatVttTime(s.End)}");
            sb.AppendLine(s.Text.Trim());
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string FormatSrtTime(TimeSpan ts)
    {
        var totalHours = (int)ts.TotalHours;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", totalHours, ts.Minutes, ts.Seconds, ts.Milliseconds);
    }

    private static string FormatVttTime(TimeSpan ts)
    {
        var totalHours = (int)ts.TotalHours;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}", totalHours, ts.Minutes, ts.Seconds, ts.Milliseconds);
    }

    private static (byte[] WavBytes, TimeSpan ProcessedDuration, bool Truncated) DecodeTo16KhzMonoWav(
        string mediaPath,
        TimeSpan? maxDuration,
        CancellationToken ct)
    {
        var targetFormat = new WaveFormat(16000, 16, 1);
        using var reader = new MediaFoundationReader(mediaPath);
        var sourceTotal = reader.TotalTime;
        var truncated = maxDuration.HasValue && sourceTotal > maxDuration.Value;

        using var resampler = new MediaFoundationResampler(reader, targetFormat)
        {
            ResamplerQuality = 60
        };

        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, targetFormat))
        {
            var maxBytes = maxDuration.HasValue
                ? (long)(maxDuration.Value.TotalSeconds * targetFormat.AverageBytesPerSecond)
                : long.MaxValue;

            var buffer = new byte[16000]; // 0.5s chunks
            long written = 0;
            int read;
            while (written < maxBytes && (read = resampler.Read(buffer, 0, (int)Math.Min(buffer.Length, maxBytes - written))) > 0)
            {
                ct.ThrowIfCancellationRequested();
                writer.Write(buffer, 0, read);
                written += read;
            }

            if (maxDuration.HasValue && written >= maxBytes && resampler.Read(buffer, 0, 32) > 0)
                truncated = true;

            writer.Flush();
            var processed = TimeSpan.FromSeconds((double)written / targetFormat.AverageBytesPerSecond);
            return (ms.ToArray(), processed, truncated);
        }
    }
}
