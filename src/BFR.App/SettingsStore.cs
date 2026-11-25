namespace BFR.App;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

public sealed class AppSettings
{
    public string Language { get; set; } = "vi";

    /// <summary>
    /// Tự động kiểm tra bản cập nhật mới (chỉ áp dụng cho bản portable;
    /// bản cài từ Microsoft Store được Store cập nhật tự động).
    /// Mặc định TẮT theo nguyên tắc opt-in.
    /// </summary>
    public bool CheckForUpdates { get; set; } = false;

    /// <summary>Nguồn cập nhật mặc định: bản phát hành mới nhất trên GitHub của dự án.</summary>
    public const string DefaultUpdateFeedUrl = "https://api.github.com/repos/mtruongforwork/Release-Batch-File-Renamer/releases/latest";

    /// <summary>URL nguồn kiểm tra phiên bản (API latest release của GitHub).</summary>
    public string UpdateFeedUrl { get; set; } = DefaultUpdateFeedUrl;

    /// <summary>
    /// Các bản trước 2.1 lưu sẵn URL giữ chỗ <c>YOUR-ORG</c> (hoặc để trống); đổi thành nguồn mặc định.
    /// URL người dùng tự nhập khác thì giữ nguyên.
    /// </summary>
    public static string NormalizeUpdateFeedUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) || url.Contains("YOUR-ORG", StringComparison.OrdinalIgnoreCase)
            ? DefaultUpdateFeedUrl
            : url;

    /// <summary>Thư mục Watch Folder đang dùng (để mở lại).</summary>
    public string? WatchFolder { get; set; }

    /// <summary>Preset dùng cho Watch Folder.</summary>
    public string? WatchPreset { get; set; }

    /// <summary>
    /// Lắc chuột khi đang kéo tệp để mở Shelf (dùng hook chuột toàn cục).
    /// Mặc định TẮT theo nguyên tắc opt-in.
    /// </summary>
    public bool ShakeToOpenShelf { get; set; } = false;

    /// <summary>Phím tắt toàn cục mở Shelf (B4). Mặc định TẮT.</summary>
    public bool ShelfHotkeyEnabled { get; set; } = false;

    /// <summary>Tổ hợp phím, ví dụ "Ctrl+Alt+Space" (xem <c>HotkeySpec.Presets</c>).</summary>
    public string ShelfHotkey { get; set; } = "Ctrl+Shift+Space";

    /// <summary>Phím tắt toàn cục mở menu tròn các công cụ. Mặc định TẮT (opt-in).</summary>
    public bool RadialHotkeyEnabled { get; set; } = false;

    public string RadialHotkey { get; set; } = "Ctrl+Alt+Q";

    /// <summary>Giữ nút chuột giữa 220ms để mở Menu Tròn ngữ cảnh v3.0 (không chặn click ngắn hay kéo pan).</summary>
    public bool MiddleMouseRadialEnabled { get; set; } = true;

    /// <summary>Thời gian giữ chuột giữa (ms) để bung Menu Tròn. Mặc định 220ms.</summary>
    public int MiddleMouseHoldMs { get; set; } = 220;

    /// <summary>Hiện nút tròn nổi (Desktop Floating Orb) trên Desktop &amp; mọi ứng dụng. Mặc định BẬT.</summary>
    public bool ShowFloatingOrb { get; set; } = true;

    /// <summary>Vị trí X đã lưu của nút tròn nổi (DIP).</summary>
    public double? FloatingOrbX { get; set; }

    /// <summary>Vị trí Y đã lưu của nút tròn nổi (DIP).</summary>
    public double? FloatingOrbY { get; set; }

    /// <summary>Danh sách khoá công cụ được người dùng gắn sao yêu thích (★) trong All Tools &amp; Smart Home.</summary>
    public List<string> FavoriteTools { get; set; } = new();

    public static int NormalizeMiddleMouseHoldMs(int ms) =>
        Math.Clamp(ms <= 0 ? 220 : ms, 120, 800);

    /// <summary>
    /// Chạy nền ở khay hệ thống: đóng cửa sổ chính chỉ ẩn đi, ứng dụng vẫn chạy
    /// (cần cho lắc chuột mở Shelf khi không mở cửa sổ). Mặc định TẮT.
    /// </summary>
    public bool RunInBackground { get; set; } = false;

    /// <summary>Giao diện: "System", "Light" hoặc "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Bảng màu nhấn: "Aurora" (mặc định), "Mint" hoặc "Sunset".</summary>
    public string AccentPalette { get; set; } = "Sunset";

    /// <summary>Hiện linh vật Tem (trang chủ, trạng thái rỗng). Mặc định bật.</summary>
    public bool ShowMascot { get; set; } = true;

    /// <summary>Giảm hiệu ứng: tắt nền mờ, đốm màu, bóng đổ và Mica. Tự áp dụng khi máy không có tăng tốc đồ hoạ. Mặc định tắt.</summary>
    public bool ReduceEffects { get; set; } = false;

    /// <summary>Giảm chuyển động (v3.2): chỉ còn mờ dần ngắn. Cài đặt Windows tắt hiệu ứng hoặc tương phản cao cũng tự bật. Mặc định tắt.</summary>
    public bool ReduceMotion { get; set; } = false;

    /// <summary>Số lần đổi tên thành công (dùng để gợi ý đánh giá Store sau 2 lần).</summary>
    public int SuccessfulRenameCount { get; set; } = 0;

    /// <summary>Đã hiển thị gợi ý đánh giá Store chưa.</summary>
    public bool HasPromptedRating { get; set; } = false;

    /// <summary>Giá hiển thị của Pro (dự phòng khi Store không trả giá). Không hard-code trong mã nguồn.</summary>
    public string? ProPriceText { get; set; }

    /// <summary>Số ngày giữ lịch sử, journal hoàn tác và nhật ký (C8). Chỉ nhận các giá trị trong <see cref="RetentionChoices"/>.</summary>
    public int HistoryRetentionDays { get; set; } = 30;

    public static readonly int[] RetentionChoices = { 7, 30, 90, 365 };

    /// <summary>Đưa giá trị bất kỳ (kể cả từ tệp bị sửa) về một lựa chọn hợp lệ gần nhất; mặc định 30.</summary>
    public static int NormalizeRetentionDays(int days) =>
        Array.IndexOf(RetentionChoices, days) >= 0 ? days : 30;

    /// <summary>Cấu trúc thông minh khi OCR (tiêu đề, bảng, nối đoạn qua trang).</summary>
    public bool OcrStructuredLayout { get; set; } = true;

    /// <summary>Preset máy chủ AI Deep OCR ("UnlimitedOcr2Sglang", "OllamaLocal", "OpenAiCompatible").</summary>
    public string AiOcrPreset { get; set; } = "UnlimitedOcr2Sglang";

    /// <summary>Base URL máy chủ AI Deep OCR do người dùng tự cấu hình.</summary>
    public string AiOcrBaseUrl { get; set; } = "http://127.0.0.1:10000";

    /// <summary>Tên model Vision OCR.</summary>
    public string AiOcrModel { get; set; } = "Unlimited-OCR-2";

    /// <summary>API Key AI Deep OCR đã mã hoá cục bộ bằng Windows DPAPI.</summary>
    public string AiOcrEncryptedApiKey { get; set; } = string.Empty;

    /// <summary>Số trang mỗi cửa sổ trượt (sliding window) cho AI Deep OCR.</summary>
    public int AiOcrWindowSize { get; set; } = 4;
}

/// <summary>
/// Lưu cài đặt người dùng (ngôn ngữ) vào %LocalAppData%\BatchFileRenamer\settings.json.
/// </summary>
public static class SettingsStore
{
    /// <summary>Phát ra khi cài đặt hiển thị linh vật Tem thay đổi để ẩn/hiện ngay không cần khởi động lại.</summary>
    public static event Action<bool>? MascotVisibilityChanged;

    private static readonly string Directory_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchFileRenamer");

    private static readonly string SettingsPath = Path.Combine(Directory_, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            settings.UpdateFeedUrl = AppSettings.NormalizeUpdateFeedUrl(settings.UpdateFeedUrl);
            settings.MiddleMouseHoldMs = AppSettings.NormalizeMiddleMouseHoldMs(settings.MiddleMouseHoldMs);
            settings.FavoriteTools ??= new List<string>();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Directory_);
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, SettingsPath, overwrite: true);
        }
        catch
        {
            // Cài đặt không phải dữ liệu quan trọng; bỏ qua lỗi ghi.
        }
    }

    public static void NotifyMascotVisibilityChanged(bool visible) => MascotVisibilityChanged?.Invoke(visible);
}
