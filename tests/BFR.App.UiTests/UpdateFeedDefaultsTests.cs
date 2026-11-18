namespace BFR.App.UiTests;

using System;
using BFR.Core.Updates;
using Xunit;

/// <summary>
/// Nguồn cập nhật mặc định: trước 2.1 là URL giữ chỗ <c>YOUR-ORG</c> nên bản portable không bao giờ kiểm tra cập nhật được.
/// </summary>
public class UpdateFeedDefaultsTests
{
    [Fact]
    public void Default_IsARealFeed_AllowedByPolicy()
    {
        Assert.DoesNotContain("YOUR-ORG", AppSettings.DefaultUpdateFeedUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AppSettings.DefaultUpdateFeedUrl, new AppSettings().UpdateFeedUrl);
        Assert.True(UpdateFeedPolicy.IsAllowedFeed(new Uri(AppSettings.DefaultUpdateFeedUrl)));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/YOUR-ORG/BatchFileRenamer/releases/latest")]
    [InlineData("https://api.github.com/repos/your-org/BatchFileRenamer/releases/latest")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void PlaceholderOrEmpty_SavedByOlderVersions_IsReplaced(string? saved)
    {
        Assert.Equal(AppSettings.DefaultUpdateFeedUrl, AppSettings.NormalizeUpdateFeedUrl(saved));
    }

    [Fact]
    public void UserChosenFeed_IsKept()
    {
        const string custom = "https://api.github.com/repos/someone/fork/releases/latest";
        Assert.Equal(custom, AppSettings.NormalizeUpdateFeedUrl(custom));
    }
}
