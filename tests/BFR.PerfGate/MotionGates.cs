namespace BFR.PerfGate;

using System.Diagnostics;
using System.Text.Json;
using BFR.Presentation.Design;

/// <summary>
/// Cổng hiệu năng chuyển động của giao diện v3.2: mở cửa sổ mẫu ở chế độ đo và chấm số khung hình rớt.
/// Chỉ chạy khi gọi <c>bfr-perf --motion</c> trên máy có màn hình và GPU, KHÔNG chạy trong CI
/// (máy CI không có tăng tốc đồ hoạ nên số đo không có ý nghĩa). Đừng thu nhỏ cửa sổ trong lúc đo.
/// </summary>
internal static class MotionGates
{
    /// <summary>Plan mục 7: mỗi chuyển cảnh rớt tối đa 1 khung hình (chấm ở phân vị 90, in kèm lần tệ nhất).</summary>
    private const double DroppedLimit = 1;

    public static IEnumerable<Program.Measurement> Run(string? appPath)
    {
        var exe = appPath ?? FindApp();
        if (exe is null || !File.Exists(exe))
        {
            return new[] { Failure("Không tìm thấy BatchFileRenamer.exe. Build BFR.App trước hoặc dùng --app=<đường dẫn>.") };
        }

        var output = Path.Combine(Path.GetTempPath(), $"bfr-motion-{Guid.NewGuid():N}.json");
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.ArgumentList.Add("--mac-gallery");
            start.ArgumentList.Add("--motion-perf");
            start.ArgumentList.Add(output);

            using var process = Process.Start(start)!;
            if (!process.WaitForExit(180_000))
            {
                process.Kill(entireProcessTree: true);
                return new[] { Failure("Quá 180 giây mà chưa đo xong.") };
            }

            return File.Exists(output) ? Parse(File.ReadAllText(output), exe) : new[] { Failure("Ứng dụng thoát mà không ghi kết quả.") };
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static IEnumerable<Program.Measurement> Parse(string json, string exe)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.GetProperty("Valid").GetBoolean()) return new[] { Failure(root.GetProperty("Note").GetString() ?? "Kết quả không hợp lệ.") };

        var budget = root.GetProperty("FrameBudgetMs").GetDouble();
        var idle = root.GetProperty("IdleFrameMs").GetDouble();
        var tier = root.GetProperty("RenderTier").GetInt32();
        var results = new List<Program.Measurement>();
        foreach (var s in root.GetProperty("Scenarios").EnumerateArray())
        {
            var name = s.GetProperty("Name").GetString()!;
            var isBaseline = name == "baseline";
            results.Add(new Program.Measurement(
                $"Chuyển động: {name} (khung rớt, p90)",
                isBaseline ? "mức nền khi đứng yên (chỉ để tham khảo)" : $"≤ {DroppedLimit} khung mỗi chuyển cảnh",
                s.GetProperty("DroppedP90").GetDouble(),
                isBaseline ? 9999 : DroppedLimit,
                $"{s.GetProperty("Transitions").GetInt32()} lần · trung vị {s.GetProperty("DroppedP50").GetDouble()} · tệ nhất {s.GetProperty("DroppedMax").GetDouble()} khung · " +
                $"khung dài nhất {s.GetProperty("WorstFrameMs").GetDouble()} ms · bố trí p90 {s.GetProperty("FirstLayoutMsP90").GetDouble()} ms · " +
                $"chuẩn {budget:F1} ms · đứng yên {idle:F1} ms · render tier {tier} · {(exe.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "không phải Release")}",
                "khung"));
        }

        return results;
    }

    private static Program.Measurement Failure(string why) => new("Chuyển động", "đo được", 1, 0, why, "lỗi");

    private static string? FindApp()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BatchFileRenamer.sln"))) dir = dir.Parent;
        if (dir is null) return null;

        return new[] { "Release", "Debug" }
            .Select(c => Path.Combine(dir.FullName, "src", "BFR.App", "bin", c, "net8.0-windows10.0.19041.0", "BatchFileRenamer.exe"))
            .FirstOrDefault(File.Exists);
    }
}
