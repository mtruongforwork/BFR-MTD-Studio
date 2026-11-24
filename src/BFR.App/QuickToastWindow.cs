namespace BFR.App;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using BFR.App.Infrastructure;
using BFR.Presentation.Tools;
using Ui = Wpf.Ui.Controls;

/// <summary>
/// Actionable Progress &amp; Result Glass Toast (v3.0):
/// - Tương thích 100% constructor v2.x cho Quick Rename.
/// - Hỗ trợ chế độ tiến trình trực tiếp (spinner + % + [Hủy]) rồi morph mượt sang chế độ Kết quả
///   (✅, huy hiệu giảm dung lượng kiểu Squoosh như ↓64%, [Mở file], [Thư mục], [Hoàn tác], [Làm tiếp ▸]).
/// </summary>
public sealed class QuickToastWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _glyph;
    private readonly TextBlock _message;
    private readonly Border _deltaBadge;
    private readonly TextBlock _deltaText;
    private readonly ProgressBar _liveProgress;
    private readonly WrapPanel _actionsPanel;
    private readonly Border _card;
    private readonly Rectangle _countdownBar;
    private readonly TranslateTransform _slide = new(0, 0);
    private readonly ScaleTransform _progressScale = new(1, 1);
    private readonly int _index;
    private bool _inProgressMode;
    private bool _popupOpen;
    private bool _closingAnimated;

    /// <summary>Constructor v2.x cho kết quả tức thì kèm nút Hoàn tác.</summary>
    public QuickToastWindow(string message, Func<string>? undo, bool isProblem, int index)
        : this(index)
    {
        ApplyResultVisuals(
            message,
            isProblem: isProblem,
            deltaBadge: null,
            outputPaths: null,
            undo: undo,
            onContinueWith: null,
            primaryActionLabel: null,
            onPrimaryAction: null,
            secondaryActionLabel: null,
            onSecondaryAction: null);
    }

    /// <summary>Tạo Toast ở chế độ đang xử lý trực tiếp (Progress + [Hủy]), chờ morph sang kết quả.</summary>
    public static QuickToastWindow CreateProgress(string title, Action? onCancel, int index = 0)
    {
        var toast = new QuickToastWindow(index)
        {
            _inProgressMode = true
        };

        toast._glyph.Text = "\uE895";
        toast._glyph.Foreground = Res("BfrAccFg", "Accent");
        toast._message.Text = title;
        toast._liveProgress.Visibility = Visibility.Visible;
        toast._liveProgress.IsIndeterminate = true;
        toast._countdownBar.Visibility = Visibility.Collapsed;

        if (onCancel is not null)
        {
            var cancelBtn = CreateActionButton(Loc.T("Cancel"), isPrimary: false);
            cancelBtn.Click += (_, _) =>
            {
                cancelBtn.IsEnabled = false;
                onCancel();
            };
            toast._actionsPanel.Children.Add(cancelBtn);
            toast._actionsPanel.Visibility = Visibility.Visible;
        }

        return toast;
    }

    private QuickToastWindow(int index)
    {
        _index = index;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        System.Windows.Automation.AutomationProperties.SetName(this, "BFR action toast");

        _glyph = new TextBlock
        {
            Text = "\uE73E",
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 18,
            Foreground = Res("BfrOk", "Success"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 12, 0)
        };

        _message = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
            Foreground = Res("BfrFg", "TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        };

        _deltaText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("BfrOk", "Success")
        };
        _deltaBadge = new Border
        {
            Background = Res("BfrOkSoft", "CardBackground"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
            Child = _deltaText
        };

        _liveProgress = new ProgressBar
        {
            Height = 4,
            Margin = new Thickness(0, 8, 0, 0),
            Minimum = 0,
            Maximum = 100,
            Visibility = Visibility.Collapsed
        };

        _actionsPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };

        var body = new StackPanel();
        body.Children.Add(_message);
        body.Children.Add(_deltaBadge);
        body.Children.Add(_liveProgress);
        body.Children.Add(_actionsPanel);

        var close = new Ui.Button
        {
            Content = "\uE711",
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            Appearance = Ui.ControlAppearance.Transparent,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Top
        };
        close.Click += (_, _) => CloseWithAnimation();

        var topGrid = new Grid { Margin = new Thickness(16, 12, 10, 12) };
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_glyph, 0);
        Grid.SetColumn(body, 1);
        Grid.SetColumn(close, 2);
        topGrid.Children.Add(_glyph);
        topGrid.Children.Add(body);
        topGrid.Children.Add(close);

        _countdownBar = new Rectangle
        {
            Height = 2,
            Fill = Res("BfrOk", "Success"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = _progressScale,
            Opacity = 0.85
        };

        var layout = new DockPanel();
        DockPanel.SetDock(_countdownBar, Dock.Bottom);
        layout.Children.Add(_countdownBar);
        layout.Children.Add(topGrid);

        _card = new Border
        {
            Margin = new Thickness(12),
            CornerRadius = new CornerRadius(12),
            Background = Res("BfrGlassFillStrong", "SurfaceBackground"),
            BorderBrush = Res("BfrGlassRim", "Divider"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            RenderTransform = _slide,
            Child = layout
        };
        if (BfrTheme.GlassEffectsEnabled)
        {
            _card.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.32, Color = Colors.Black };
        }

        Content = _card;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => CloseWithAnimation();

        MouseEnter += (_, _) =>
        {
            if (_inProgressMode) return;
            _timer.Stop();
            StopCountdownBar(resetFull: false);
        };
        MouseLeave += (_, _) =>
        {
            if (_inProgressMode || _popupOpen) return;
            _timer.Stop();
            _timer.Start();
            StartCountdownBar(_timer.Interval);
        };
        Closed += (_, _) => _timer.Stop();

        Loaded += (_, _) =>
        {
            Reposition();
            PlayEntrance();
            if (!_inProgressMode)
            {
                _timer.Start();
                StartCountdownBar(_timer.Interval);
            }
        };
    }

    /// <summary>Cập nhật tiến trình trực tiếp từ bất kỳ luồng nào.</summary>
    public void UpdateProgress(double? percent, string statusText)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateProgress(percent, statusText));
            return;
        }

        if (_closingAnimated || !_inProgressMode) return;
        _message.Text = statusText;
        if (percent.HasValue)
        {
            _liveProgress.IsIndeterminate = false;
            _liveProgress.Value = Math.Clamp(percent.Value, 0, 100);
        }
        else
        {
            _liveProgress.IsIndeterminate = true;
        }
    }

    /// <summary>Morph từ chế độ Tiến trình sang chế độ Hoàn tất kèm các nút hành động &amp; Làm tiếp ▸.</summary>
    public void CompleteSuccess(
        string message,
        string? deltaBadge = null,
        IReadOnlyList<string>? outputPaths = null,
        Func<string>? undo = null,
        Action<ContinueWithAction, IReadOnlyList<string>>? onContinueWith = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => CompleteSuccess(message, deltaBadge, outputPaths, undo, onContinueWith));
            return;
        }

        _inProgressMode = false;
        _liveProgress.Visibility = Visibility.Collapsed;
        ApplyResultVisuals(message, isProblem: false, deltaBadge, outputPaths, undo, onContinueWith, null, null, null, null);
        RestartCountdown(TimeSpan.FromSeconds(12));
        Reposition();
    }

    /// <summary>Morph sang trạng thái cảnh báo/lỗi kèm tối đa 2 nút giải quyết tại chỗ.</summary>
    public void CompleteError(
        string message,
        string? primaryActionLabel = null,
        Action? onPrimaryAction = null,
        string? secondaryActionLabel = null,
        Action? onSecondaryAction = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => CompleteError(message, primaryActionLabel, onPrimaryAction, secondaryActionLabel, onSecondaryAction));
            return;
        }

        _inProgressMode = false;
        _liveProgress.Visibility = Visibility.Collapsed;
        ApplyResultVisuals(message, isProblem: true, null, null, null, null,
            primaryActionLabel, onPrimaryAction, secondaryActionLabel, onSecondaryAction);
        RestartCountdown(TimeSpan.FromSeconds(15));
        Reposition();
    }

    private void ApplyResultVisuals(
        string message,
        bool isProblem,
        string? deltaBadge,
        IReadOnlyList<string>? outputPaths,
        Func<string>? undo,
        Action<ContinueWithAction, IReadOnlyList<string>>? onContinueWith,
        string? primaryActionLabel,
        Action? onPrimaryAction,
        string? secondaryActionLabel,
        Action? onSecondaryAction)
    {
        var accent = isProblem ? Res("BfrWarn", "Warning") : Res("BfrOk", "Success");
        _glyph.Text = isProblem ? "\uE7BA" : "\uE73E";
        _glyph.Foreground = accent;
        _message.Text = message;
        _countdownBar.Fill = accent;
        _countdownBar.Visibility = Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(deltaBadge))
        {
            _deltaText.Text = deltaBadge;
            _deltaBadge.Visibility = Visibility.Visible;
        }
        else
        {
            _deltaBadge.Visibility = Visibility.Collapsed;
        }

        _actionsPanel.Children.Clear();

        if (!string.IsNullOrWhiteSpace(primaryActionLabel) && onPrimaryAction is not null)
        {
            var btn = CreateActionButton(primaryActionLabel, isPrimary: true);
            btn.Click += (_, _) =>
            {
                onPrimaryAction();
                CloseWithAnimation();
            };
            _actionsPanel.Children.Add(btn);
        }

        if (!string.IsNullOrWhiteSpace(secondaryActionLabel) && onSecondaryAction is not null)
        {
            var btn = CreateActionButton(secondaryActionLabel, isPrimary: false);
            btn.Click += (_, _) =>
            {
                onSecondaryAction();
                CloseWithAnimation();
            };
            _actionsPanel.Children.Add(btn);
        }

        var existingOutputs = outputPaths?.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
        if (existingOutputs is { Count: > 0 })
        {
            var first = existingOutputs[0];

            var openBtn = CreateActionButton(Loc.T("ToastOpenFile"), isPrimary: false);
            openBtn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo { FileName = first, UseShellExecute = true }); } catch { }
            };
            _actionsPanel.Children.Add(openBtn);

            var folderBtn = CreateActionButton(Loc.T("ToastOpenFolder"), isPrimary: false);
            folderBtn.Click += (_, _) =>
            {
                try
                {
                    var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    using var _ = Process.Start(new ProcessStartInfo { FileName = explorer, Arguments = $"/select,\"{first}\"", UseShellExecute = false });
                }
                catch { }
            };
            _actionsPanel.Children.Add(folderBtn);
        }

        if (undo is not null)
        {
            var undoBtn = CreateActionButton(Loc.T("QuickUndo"), isPrimary: false);
            System.Windows.Automation.AutomationProperties.SetName(undoBtn, "Undo action");
            undoBtn.Click += (_, _) =>
            {
                undoBtn.IsEnabled = false;
                _message.Text = undo();
                undoBtn.Visibility = Visibility.Collapsed;
                RestartCountdown(TimeSpan.FromSeconds(4));
            };
            _actionsPanel.Children.Add(undoBtn);
        }

        if (existingOutputs is { Count: > 0 } && onContinueWith is not null)
        {
            var suggestions = ContinueWithCatalog.GetSuggestions(existingOutputs);
            if (suggestions.Count > 0)
            {
                var continueBtn = CreateActionButton(Loc.T("ToastContinueWith") + " ▸", isPrimary: true);
                continueBtn.Click += (_, _) =>
                {
                    var menu = new ContextMenu();
                    foreach (var suggestion in suggestions)
                    {
                        var captured = suggestion;
                        var item = new MenuItem { Header = Loc.T(suggestion.LabelKey) };
                        item.Click += (_, _) =>
                        {
                            onContinueWith(captured, existingOutputs);
                            CloseWithAnimation();
                        };
                        menu.Items.Add(item);
                    }
                    menu.Opened += (_, _) => { _popupOpen = true; _timer.Stop(); StopCountdownBar(false); };
                    menu.Closed += (_, _) => { _popupOpen = false; RestartCountdown(TimeSpan.FromSeconds(6)); };
                    menu.PlacementTarget = continueBtn;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                    menu.IsOpen = true;
                };
                _actionsPanel.Children.Add(continueBtn);
            }
        }

        _actionsPanel.Visibility = _actionsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _timer.Interval = TimeSpan.FromSeconds(isProblem ? 14 : 9);
    }

    private static Ui.Button CreateActionButton(string text, bool isPrimary) => new()
    {
        Content = text,
        Appearance = isPrimary ? Ui.ControlAppearance.Primary : Ui.ControlAppearance.Secondary,
        Padding = new Thickness(10, 4, 10, 4),
        Margin = new Thickness(0, 0, 6, 4),
        FontSize = 11.5
    };

    private static Brush Res(string primary, string fallback)
    {
        if (Application.Current?.TryFindResource(primary) is Brush b) return b;
        if (Application.Current?.TryFindResource(fallback) is Brush fb) return fb;
        return Brushes.Transparent;
    }

    private void Reposition()
    {
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 8;
        Top = Math.Max(area.Top + 8, area.Bottom - ActualHeight - 8 - _index * (ActualHeight + 4));
    }

    private void RestartCountdown(TimeSpan duration)
    {
        _timer.Stop();
        _timer.Interval = duration;
        _timer.Start();
        StartCountdownBar(duration);
    }

    private void PlayEntrance()
    {
        if (BfrTheme.ReduceMotion) return;
        _card.Opacity = 0;
        _slide.X = 28;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
    }

    private void StartCountdownBar(TimeSpan interval)
    {
        if (BfrTheme.ReduceMotion)
        {
            _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _progressScale.ScaleX = 1;
            return;
        }
        _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, interval));
    }

    private void StopCountdownBar(bool resetFull)
    {
        var current = _progressScale.ScaleX;
        _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _progressScale.ScaleX = resetFull ? 1 : current;
    }

    public void CloseWithAnimation()
    {
        if (_closingAnimated) return;
        _closingAnimated = true;
        _timer.Stop();

        if (BfrTheme.ReduceMotion)
        {
            try { Close(); } catch (InvalidOperationException) { }
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(_card.Opacity, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        fade.Completed += (_, _) => { try { Close(); } catch (InvalidOperationException) { } };
        _card.BeginAnimation(OpacityProperty, fade);
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 20, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
    }
}
