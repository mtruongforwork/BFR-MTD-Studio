namespace BFR.Media.Tests;

using System;
using BFR.Core.Ai;
using BFR.Media;
using Xunit;

public sealed class OfflineAudioTranscriberTests
{
    [Fact]
    public void FormatSrt_ProducesValidSubRipTimestamps()
    {
        var segments = new[]
        {
            new TranscriptSegment(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3.25), "Xin chào mọi người."),
            new TranscriptSegment(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(5)), TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(9.1)), "Hôm nay họp tổng kết.")
        };

        var srt = OfflineAudioTranscriber.FormatSrt(segments);

        Assert.Contains("1\r\n00:00:00,500 --> 00:00:03,250\r\nXin chào mọi người.", srt.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"));
        Assert.Contains("01:00:05,000 --> 01:00:09,100", srt);
    }

    [Fact]
    public void FormatVtt_StartsWithWebVttHeader_AndUsesDotMilliseconds()
    {
        var segments = new[]
        {
            new TranscriptSegment(TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(4.8), "Hello world.")
        };

        var vtt = OfflineAudioTranscriber.FormatVtt(segments);

        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("00:00:01.200 --> 00:00:04.800", vtt);
        Assert.Contains("Hello world.", vtt);
    }

    [Fact]
    public void IsSupportedMediaExtension_RecognizesCommonAudioVideoFormats()
    {
        Assert.True(OfflineAudioTranscriber.IsSupportedMediaExtension(".mp4"));
        Assert.True(OfflineAudioTranscriber.IsSupportedMediaExtension(".MP3"));
        Assert.True(OfflineAudioTranscriber.IsSupportedMediaExtension(".m4a"));
        Assert.True(OfflineAudioTranscriber.IsSupportedMediaExtension(".wav"));
        Assert.False(OfflineAudioTranscriber.IsSupportedMediaExtension(".pdf"));
        Assert.False(LocalModelCatalog.WhisperTiny.RequiresPro);
        Assert.True(LocalModelCatalog.WhisperBase.RequiresPro);
        Assert.True(LocalModelCatalog.WhisperSmall.RequiresPro);
    }
}
