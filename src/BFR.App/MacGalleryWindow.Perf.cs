namespace BFR.App;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BFR.App.Infrastructure;
using BFR.Presentation.Design;

/// <summary>
/// Đo khung hình của các chuyển động (plan v3.2, mục 7). Chạy bằng
/// <c>BatchFileRenamer.exe --mac-gallery --motion-perf kết-quả.json</c>; được <c>bfr-perf --motion</c> gọi.
/// </summary>
public partial class MacGalleryWindow
{
    private const int LargeRowCount = 5_000;

    /// <summary>
    /// Chuẩn cố định để đếm khung rớt: 60 khung/giây là nhịp animation mặc định của WPF.
    /// Không tự ước lượng từ lúc đứng yên vì nhịp đó dao động giữa các lần chạy, làm cổng lúc chặt lúc lỏng.
    /// </summary>
    private const double FrameBudgetMs = 1000d / 60;

    /// <summary>Thời gian theo dõi mỗi chuyển cảnh: đủ dài hơn lò xo 380 ms.</summary>
    private const int WindowMilliseconds = 450;

    private sealed record ScenarioResult(string Name, int Transitions, double DroppedP50, double DroppedP90, double DroppedMax,
        double WorstFrameMs, double FirstLayoutMsP90, int MinFrames);

    private sealed record PerfReport(double FrameBudgetMs, double IdleFrameMs, int RenderTier, bool ReducedMotion, bool Valid, string Note, List<ScenarioResult> Scenarios);

    /// <summary>Gom thời điểm các khung hình được dựng. Đăng ký Rendering cũng buộc WPF dựng liên tục.</summary>
    private sealed class FrameRecorder : IDisposable
    {
        private TimeSpan _last = TimeSpan.MinValue;

        public FrameRecorder() => CompositionTarget.Rendering += OnRendering;

        public List<double> Times { get; } = new();

        private void OnRendering(object? sender, EventArgs e)
        {
            var time = ((RenderingEventArgs)e).RenderingTime;
            if (time == _last) return;   // WPF có thể phát hai lần cho cùng một khung hình
            _last = time;
            Times.Add(time.TotalMilliseconds);
        }

        public List<double> Intervals => Times.Zip(Times.Skip(1), (a, b) => b - a).ToList();

        public void Dispose() => CompositionTarget.Rendering -= OnRendering;
    }

    public async Task RunMotionPerfAsync(string outputPath)
    {
        var scenarios = new List<ScenarioResult>();
        var idle = 0d;
        var note = string.Empty;
        try
        {
            Motion.UserReduced = false;
            if (Motion.Reduced)
                throw new InvalidOperationException("Windows đang tắt hiệu ứng hoặc bật tương phản cao: không có chuyển động để đo.");
            Activate();
            StartHeartbeat();
            await Task.Delay(1500);

            using (var recorder = new FrameRecorder())
            {
                await Task.Delay(1500);
                idle = FrameStats.Percentile(recorder.Intervals, 50);
            }

            if (idle <= 0) throw new InvalidOperationException("Không đo được nhịp dựng khung (cửa sổ không được dựng).");
            if (idle > FrameBudgetMs * 1.5)
                throw new InvalidOperationException($"Đứng yên mà mỗi khung đã mất {idle:F1} ms (máy đang bận hoặc không giữ được 60 khung/giây): đóng bớt ứng dụng rồi đo lại.");

            // Mức nền: cửa sổ đứng yên, để biết bao nhiêu khung rớt là do máy chứ không phải do hiệu ứng.
            scenarios.Add(await MeasureAsync("baseline", 10, _ => { }, FrameBudgetMs));

            var pages = new[] { NavHome, NavControls, NavRename };
            // Một vòng làm nóng: lần đầu dựng trang gồm cả biên dịch template, không phải chi phí chuyển cảnh.
            foreach (var page in pages)
            {
                page.IsChecked = true;
                await Task.Delay(WindowMilliseconds);
            }

            scenarios.Add(await MeasureAsync("navigate", 20, i => pages[i % pages.Length].IsChecked = true, FrameBudgetMs));

            _rows = Enumerable.Range(0, LargeRowCount).Select(i => Rows[i % Rows.Length] with { Head = $"{i:0000} {Rows[i % Rows.Length].Head}" }).ToArray();
            NavRename.IsChecked = true;
            ApplyFilter(animate: false);
            await Task.Delay(1500);

            scenarios.Add(await MeasureAsync("inspector-5000", 10, _ => ToggleInspector_Click(this, new RoutedEventArgs()), FrameBudgetMs));
            scenarios.Add(await MeasureAsync("sidebar-5000", 10, _ => ToggleSidebar_Click(this, new RoutedEventArgs()), FrameBudgetMs));
        }
        catch (Exception ex)
        {
            note = ex.Message;
        }

        var valid = note.Length == 0 && WindowState != WindowState.Minimized && scenarios.All(s => s.MinFrames >= 5);
        if (!valid && note.Length == 0) note = "Cửa sổ bị thu nhỏ hoặc quá ít khung hình: kết quả không dùng được.";

        var report = new PerfReport(FrameBudgetMs, Math.Round(idle, 1), RenderCapability.Tier >> 16, Motion.Reduced, valid, note, scenarios);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Khi không có animation nào, WPF dựng khung theo đồng hồ hệ thống (15,6 ms hoặc mịn hơn tuỳ ứng dụng khác đang chạy),
    /// nên khoảng lặng sau mỗi chuyển cảnh bị tính nhầm là khung rớt. Một animation vô hình chạy suốt lúc đo
    /// giữ bộ dựng hình luôn hoạt động như trong lúc chuyển cảnh.
    /// </summary>
    private void StartHeartbeat()
    {
        var dot = new System.Windows.Shapes.Rectangle { Width = 1, Height = 1, Fill = Brushes.Black, Opacity = 0.01, IsHitTestVisible = false };
        System.Windows.Controls.Grid.SetColumnSpan(dot, 2);
        dot.HorizontalAlignment = HorizontalAlignment.Right;
        dot.VerticalAlignment = VerticalAlignment.Bottom;
        ((System.Windows.Controls.Grid)Content).Children.Add(dot);
        dot.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.01, 0.02, TimeSpan.FromMilliseconds(500))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        });
    }

    private async Task<ScenarioResult> MeasureAsync(string name, int count, Action<int> action, double refresh)
    {
        var dropped = new List<double>();
        var layout = new List<double>();
        var worst = 0d;
        var minFrames = int.MaxValue;

        for (var i = 0; i < count; i++)
        {
            using var recorder = new FrameRecorder();
            await Task.Delay(100);
            var before = recorder.Times.Count;

            var watch = Stopwatch.StartNew();
            action(i);
            // Đợi luồng UI làm xong việc dựng và bố trí do thao tác gây ra.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            layout.Add(watch.Elapsed.TotalMilliseconds);

            await Task.Delay(WindowMilliseconds);

            // Tính từ khung ngay trước thao tác để không bỏ sót cú khựng đầu tiên.
            var times = recorder.Times.Skip(Math.Max(0, before - 1)).ToList();
            var intervals = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
            dropped.Add(FrameStats.Dropped(intervals, refresh));
            if (intervals.Count > 0) worst = Math.Max(worst, intervals.Max());
            minFrames = Math.Min(minFrames, intervals.Count);
        }

        return new ScenarioResult(name, count, FrameStats.Percentile(dropped, 50), FrameStats.Percentile(dropped, 90),
            dropped.Max(), Math.Round(worst, 1), Math.Round(FrameStats.Percentile(layout, 90), 1), minFrames);
    }
}
