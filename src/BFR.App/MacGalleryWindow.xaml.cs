namespace BFR.App;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BFR.App.Controls.Mac;
using BFR.App.Infrastructure;
using BFR.Presentation.Design;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

/// <summary>
/// Prototype P0 của giao diện kiểu macOS (docs/Plan/BFR_MACOS_NATIVE_UI_PLAN.md): khung ba vùng, các control chính
/// và bốn chuyển động M1 (đổi trang), M3 (thu/mở sidebar, inspector), M4 (segmented), M10 (sheet).
/// Mở bằng <c>BatchFileRenamer.exe --mac-gallery</c>. Chỉ có dữ liệu giả.
/// </summary>
public partial class MacGalleryWindow : FluentWindow
{
    private const double SidebarWidth = 232;
    private const double InspectorWidth = 300;

    /// <summary>Chỗ chừa cho traffic light trên toolbar khi sidebar đã thu.</summary>
    private const double LightsWidth = 66;

    private sealed record Row(string Head, string Removed, string Added, string Tail, bool Changes)
    {
        public string Status => Changes ? "Sẽ đổi" : "Không đổi";

        public string OldName => Head + Removed + Tail;

        public string NewName => Head + Added + Tail;
    }

    private static readonly Row[] Rows =
    {
        new("Báo cáo ", "-tháng 5-", "tháng 5", ".docx", true),
        new("da lat 2026", " ", "", ".mp4", true),
        new("Danh sách khách mời", " - FINAL", "", ".xlsx", true),
        new("IMG_0412", " (copy)", "", ".JPG", true),
        new("IMG_0413", " (copy)", "", ".JPG", true),
        new("IMG_0414", "", "", ".JPG", false),
        new("notes ", " ", "", "trip.txt", true),
        new("README", "", "", ".md", false)
    };

    private IReadOnlyList<Row> _rows = Rows;
    private FrameworkElement? _page;
    private int _navIndex;
    private bool _sidebarOpen = true;
    private bool _inspectorWanted = true;
    private bool _dark;
    private MacAccent _accent = MacAccents.Default;

    public MacGalleryWindow()
    {
        InitializeComponent();

        _dark = ThemeManager.SystemUsesDarkTheme();
        ThemeSegment.SelectedIndex = _dark ? 1 : 0;
        ThemeSegment.SelectionChanged += _ => SetTheme(ThemeSegment.SelectedIndex == 1);
        FilterSegment.SelectionChanged += _ => ApplyFilter(animate: true);
        InspectorSegment.SelectionChanged += old => ShowInspectorTab(InspectorSegment.SelectedIndex, old);
        RenamePage.SelectionChanged += (_, _) => UpdateTrace();
        ReduceMotionSwitch.IsChecked = Motion.Reduced;
        // Windows đã tắt hiệu ứng (hoặc tương phản cao) thì công tắc này không bật lại được.
        ReduceMotionSwitch.IsEnabled = !Motion.Reduced || Motion.UserReduced;

        BuildFavorites();
        BuildRecents();
        BuildRuleRows();
        BuildAccentSwatches();
        BuildLongList();
        ApplyFilter(animate: false);

        Loaded += (_, _) => SetTheme(_dark);
        MacTheme.TrackEmphasis(this);
        PreviewKeyDown += OnShortcut;

        NavRename.IsChecked = true;
    }

    // ---------------- Theme ----------------

    private void SetTheme(bool dark)
    {
        _dark = dark;
        ApplyPalette();
    }

    private void ApplyPalette()
    {
        MacTheme.Apply(this, _dark, _accent);

        // Đồng bộ WPF-UI để Mica Alt và phần chrome của cửa sổ theo đúng sáng/tối.
        ApplicationThemeManager.Apply(_dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Tabbed, updateAccent: false);

        // Win10, tương phản cao hoặc máy không bật được hiệu ứng nền: sidebar dùng nền đặc.
        var translucent = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && BfrTheme.GlassEffectsEnabled && !MacTheme.IsHighContrast;
        SidebarSurface.SetResourceReference(Border.BackgroundProperty, translucent ? "Mac.SidebarTint" : "Mac.SidebarSolid");
    }

    // ---------------- Điều hướng (M1) ----------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        var item = (RadioButton)sender;
        var index = NavItems(SidebarItems).ToList().IndexOf(item);
        var direction = index >= _navIndex ? 1 : -1;
        _navIndex = index;

        FrameworkElement next = item == NavRename ? RenamePage
            : item == NavHome ? HomePage
            : item == NavControls ? ControlsPage
            : PlaceholderPage;

        var title = (string)item.Content;
        if (next == PlaceholderPage)
        {
            PlaceholderTitle.Text = title;
            PlaceholderIcon.Data = (Geometry?)item.Tag;
        }

        // Chọn mục phía dưới thì trang mới đi từ dưới lên, và ngược lại.
        Motion.SwitchPage(PageHost, next, 0, 12 * direction, animate: _page is not null);

        TitleText.Text = title;
        var isRename = next == RenamePage;
        CenterToolbar.Visibility = RenameToolbar.Visibility = InspectorButton.Visibility = isRename ? Visibility.Visible : Visibility.Collapsed;
        CountsPanel.Visibility = isRename ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Visibility = isRename ? Visibility.Collapsed : Visibility.Visible;
        SetInspector(isRename && _inspectorWanted);

        if (_page is not null)
        {
            Motion.FadeIn(TitleText, MotionKind.Standard);
            if (isRename)
            {
                Motion.Enter(CenterToolbar, 0, 0);
                Motion.Enter(RenameToolbar, 0, 0);
            }
        }

        _page = next;
    }

    private static IEnumerable<RadioButton> NavItems(Panel panel)
    {
        foreach (var child in panel.Children)
        {
            if (child is RadioButton item) yield return item;
            else if (child is Panel group)
                foreach (var nested in NavItems(group)) yield return nested;
        }
    }

    // ---------------- Kéo thả (M6) và nhập sai (M13) ----------------

    private void DropZone_DragEnter(object sender, DragEventArgs e) => Motion.Breathe(DropZone, true);

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave cũng nổi lên khi chuột đi qua phần tử con: chỉ dừng khi đã ra khỏi cả vùng.
        var p = e.GetPosition(DropZone);
        if (e.RoutedEvent == DragDrop.DropEvent || p.X <= 0 || p.Y <= 0 || p.X >= DropZone.ActualWidth || p.Y >= DropZone.ActualHeight)
            Motion.Breathe(DropZone, false);
    }

    private void WrongPassword_Click(object sender, RoutedEventArgs e)
    {
        PasswordError.Visibility = Visibility.Visible;
        Motion.Shake(DemoPassword);
        DemoPassword.Focus();
        DemoPassword.SelectAll();
    }

    private void Password_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) WrongPassword_Click(sender, e);
    }

    // ---------------- Sidebar và inspector (M3) ----------------

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sidebarOpen = !_sidebarOpen;
        Motion.AnimateWidth(SidebarClip, _sidebarOpen ? SidebarWidth : 0);
        Motion.AnimateWidth(LightsSpacer, _sidebarOpen ? 0 : LightsWidth);
        SidebarItems.IsEnabled = _sidebarOpen;   // không Tab được vào sidebar đã ẩn
    }

    private void ToggleInspector_Click(object sender, RoutedEventArgs e)
    {
        _inspectorWanted = !_inspectorWanted;
        SetInspector(_inspectorWanted);
    }

    private void SetInspector(bool open)
    {
        var target = open ? InspectorWidth : 0;
        InspectorClip.IsEnabled = open;
        if (!IsLoaded) InspectorClip.Width = target;
        else if (Math.Abs(InspectorClip.Width - target) > 0.5) Motion.AnimateWidth(InspectorClip, target);
    }

    // ---------------- Segmented (M4) ----------------

    private void ShowInspectorTab(int index, int oldIndex)
    {
        FrameworkElement next = index == 0 ? RulesPanel : TracePanel;
        RulesPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        TracePanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        Motion.Enter(next, index > oldIndex ? 18 : -18, 0);
    }

    private void ApplyFilter(bool animate)
    {
        var query = PreviewSearch.Text.Trim();
        var shown = _rows
            .Where(r => FilterSegment.SelectedIndex == 0 || r.Changes == (FilterSegment.SelectedIndex == 1))
            .Where(r => query.Length == 0 || r.OldName.Contains(query, StringComparison.CurrentCultureIgnoreCase) || r.NewName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        RenamePage.ItemsSource = shown;
        Motion.SetCountValue(CountTotal, shown.Count);
        Motion.SetCountValue(CountChange, shown.Count(r => r.Changes));
        Motion.SetCountValue(CountSame, shown.Count(r => !r.Changes));
        if (RenamePage.Items.Count > 0) RenamePage.SelectedIndex = Math.Min(1, RenamePage.Items.Count - 1);
        if (animate) Motion.Enter(RenamePage, 0, 6);
    }

    private void UpdateTrace()
    {
        var row = RenamePage.SelectedItem as Row;
        TraceOld.Text = row?.OldName ?? "Chưa chọn dòng nào";
        TraceNew.Text = row is null ? "" : row.Changes ? "Kết quả → " + row.NewName : "Không quy tắc nào đổi tên này";
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter(animate: false);

    // ---------------- Sheet (M10) ----------------

    private void ShowSheet_Click(object sender, RoutedEventArgs e)
    {
        SheetLayer.Visibility = Visibility.Visible;
        SheetLayer.IsHitTestVisible = true;
        SheetLayer.UpdateLayout();

        SheetDim.Opacity = 0;
        Motion.To(SheetDim, OpacityProperty, 1, MotionKind.Standard);
        Sheet.Opacity = Motion.Reduced ? 0 : 1;
        Motion.To(Sheet, OpacityProperty, 1, MotionKind.Standard);
        SheetY.BeginAnimation(TranslateTransform.YProperty, null);
        SheetY.Y = Motion.Reduced ? 0 : -Sheet.ActualHeight - 20;
        // Lò xo Standard để sheet không nảy quá mép toolbar.
        Motion.To(SheetY, TranslateTransform.YProperty, 0, MotionKind.Standard);
        SheetOk.Focus();
    }

    private void CloseSheet_Click(object sender, RoutedEventArgs e)
    {
        SheetLayer.IsHitTestVisible = false;
        Motion.To(SheetY, TranslateTransform.YProperty, Motion.Reduced ? 0 : -Sheet.ActualHeight - 20, MotionKind.Exit);
        Motion.To(Sheet, OpacityProperty, Motion.Reduced ? 0 : 1, MotionKind.Exit);
        Motion.To(SheetDim, OpacityProperty, 0, MotionKind.Exit, () =>
        {
            // Người dùng có thể đã mở lại sheet trong lúc đang đóng.
            if (!SheetLayer.IsHitTestVisible) SheetLayer.Visibility = Visibility.Collapsed;
        });
    }

    // ---------------- Cửa sổ và phím tắt ----------------

    private void OnShortcut(object sender, KeyEventArgs e)
    {
        // Sheet đang mở thì nó giữ bàn phím.
        if (SheetLayer.Visibility == Visibility.Visible) return;
        if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Alt)) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.S) ToggleSidebar_Click(this, e);
        else if (key == Key.I && InspectorButton.Visibility == Visibility.Visible) ToggleInspector_Click(this, e);
        else return;
        e.Handled = true;
    }

    private void ReduceMotion_Changed(object sender, RoutedEventArgs e) => Motion.UserReduced = ReduceMotionSwitch.IsChecked == true;

    private void PullDown_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Chỉ kéo khi bấm vào nền, không phải vào control đặt trên toolbar.
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (e.ClickCount == 2) Maximize_Click(sender, e);
        else DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---------------- Nội dung giả ----------------

    private TextBlock Text(string text, string style) => new() { Text = text, Style = (Style)FindResource(style) };

    private MacIcon MakeIcon(string key, double size, string? brush = null)
    {
        var icon = new MacIcon { Size = size };
        icon.SetResourceReference(MacIcon.DataProperty, "Icon." + key);
        if (brush is not null) icon.SetResourceReference(MacIcon.ForegroundProperty, brush);
        return icon;
    }

    private void BuildFavorites()
    {
        var items = new (string Title, string Icon, string Color)[]
        {
            ("Đổi tên", "Type", "#8944AB"), ("Gộp PDF", "Layers", "#E0443A"), ("Nén PDF", "Minimize2", "#E8710A"),
            ("OCR", "ScanText", "#0A7EA4"), ("Ảnh", "Image", "#1E9E4A"), ("Tệp trùng", "Copy", "#006ADC")
        };

        foreach (var (title, key, color) in items)
        {
            var tile = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(13), Background = MacTheme.Solid(color) };
            var glyph = MakeIcon(key, 28);
            glyph.Foreground = Brushes.White;
            tile.Child = glyph;

            var stack = new StackPanel();
            stack.Children.Add(tile);
            var label = Text(title, "Mac.Text");
            label.FontSize = 12;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(label);

            var button = new Button { Content = stack, Style = (Style)FindResource("Mac.ToolbarButton"), Height = 92, Width = 90, Margin = new Thickness(0, 0, 10, 8) };
            Motion.SetHoverLift(button, 3);
            AutomationProperties.SetName(button, "Yêu thích " + title);
            Favorites.Children.Add(button);
        }
    }

    private void BuildRecents()
    {
        var items = new[] { ("Đổi tên 24 ảnh trong Da Lat", "10 phút trước", "Type"), ("Nén Hợp đồng.pdf · 14,2 MB → 4,5 MB", "Hôm nay", "Minimize2"), ("OCR 3 trang quét", "Hôm qua", "ScanText") };
        for (var i = 0; i < items.Length; i++)
        {
            if (i > 0) Recents.Children.Add(new Border { Style = (Style)FindResource("Mac.RowLine") });
            var (title, when, key) = items[i];
            var row = new DockPanel { Height = 40 };
            var time = Text(when, "Mac.Callout");
            time.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(time, Dock.Right);
            row.Children.Add(time);
            var icon = MakeIcon(key, 16, "Mac.Accent");
            icon.Margin = new Thickness(0, 0, 10, 0);
            row.Children.Add(icon);
            var name = Text(title, "Mac.Text");
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            Recents.Children.Add(row);
        }
    }

    private void BuildRuleRows()
    {
        var rules = new[] { ("Tìm và thay", "“ (copy)” → trống"), ("Tìm và thay", "“ - FINAL” → trống"), ("Cắt khoảng trắng", "Gộp và cắt hai đầu") };
        for (var i = 0; i < rules.Length; i++)
        {
            if (i > 0) RuleRows.Children.Add(new Border { Style = (Style)FindResource("Mac.RowLine") });
            var (title, summary) = rules[i];
            var row = new DockPanel { Height = 46 };

            var toggle = new System.Windows.Controls.Primitives.ToggleButton { Style = (Style)FindResource("Mac.Switch"), IsChecked = true };
            AutomationProperties.SetName(toggle, "Bật quy tắc " + title);
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(toggle);

            var grip = MakeIcon("GripVertical", 14, "Mac.Label4");
            grip.Margin = new Thickness(-4, 0, 8, 0);
            row.Children.Add(grip);

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(Text(title, "Mac.Text"));
            var caption = Text(summary, "Mac.Callout");
            caption.FontSize = 11;
            stack.Children.Add(caption);
            row.Children.Add(stack);
            RuleRows.Children.Add(row);
        }
    }

    private void BuildAccentSwatches()
    {
        foreach (var accent in MacAccents.All)
        {
            var button = new Button
            {
                Style = (Style)FindResource("Mac.TrafficDot"), Background = MacTheme.Solid(accent.LightTint),
                Width = 18, Height = 18, Margin = new Thickness(0, 0, 12, 0), ToolTip = accent.Id
            };
            AutomationProperties.SetName(button, "Màu nhấn " + accent.Id);
            button.Click += (_, _) =>
            {
                _accent = accent;
                ApplyPalette();
            };
            AccentSwatches.Children.Add(button);
        }
    }

    /// <summary>Danh sách đủ dài để thử thanh cuộn overlay.</summary>
    private void BuildLongList()
    {
        for (var i = 1; i <= 40; i++) LongList.Items.Add($"Lần đổi tên #{41 - i}  ·  {i * 3} tệp");
        LongList.SelectedIndex = 1;
    }
}
