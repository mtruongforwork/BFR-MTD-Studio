namespace BFR.App;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using BFR.App.Controls.Mac;
using BFR.App.Infrastructure;
using BFR.Presentation;
using BFR.Presentation.Design;

/// <summary>
/// Trang Đổi tên kiểu macOS (plan v3.2, P4): điều khiển trên toolbar, chuỗi quy tắc và vết quy tắc trong inspector,
/// kéo thả sắp xếp quy tắc có nhường chỗ (M7), sheet xác nhận (M10) và thông báo sau khi áp dụng (M12).
/// </summary>
public partial class MainWindow
{
    /// <summary>Thời gian thông báo thành công tự ẩn. Hoàn tác vẫn còn ở thanh trạng thái.</summary>
    private static readonly TimeSpan BannerLifetime = TimeSpan.FromSeconds(10);

    private sealed class RenameShellContent : IMacShellContent
    {
        public required FrameworkElement ToolbarCenter { get; init; }

        public required FrameworkElement ToolbarTrailing { get; init; }

        public required FrameworkElement Inspector { get; init; }

        public string? StatusText => null;   // trang Đổi tên tự vẽ thanh trạng thái vì có nút Hoàn tác và Huỷ

        public event EventHandler? ShellContentChanged
        {
            add { }
            remove { }
        }
    }

    private RenameShellContent? _renameShell;
    private DispatcherTimer? _bannerTimer;

    private void InitializeRenameShell()
    {
        RenameParts.Children.Remove(RenameToolbarCenter);
        RenameParts.Children.Remove(RenameToolbarTrailing);
        RenameParts.Children.Remove(RenameInspector);
        _renameShell = new RenameShellContent
        {
            ToolbarCenter = RenameToolbarCenter,
            ToolbarTrailing = RenameToolbarTrailing,
            Inspector = RenameInspector
        };

        SheetHost.Blocked = ShellRoot;

        InspectorTabs.SelectionChanged += previous => ShowInspectorTab(previous);

        _bannerTimer = new DispatcherTimer { Interval = BannerLifetime };
        _bannerTimer.Tick += (_, _) =>
        {
            // Đang rê chuột hoặc đang dùng bàn phím trong thông báo thì đợi thêm.
            if (SuccessBanner.IsMouseOver || SuccessBanner.IsKeyboardFocusWithin) return;
            HideSuccessBanner();
        };

        // Cửa sổ hẹp hoặc chữ dài (tiếng Pháp): co ô lọc rồi ô sắp xếp để không nút nào bị che.
        Toolbar.SizeChanged += (_, _) => FitRenameToolbarLater();
        RenameToolbarCenter.SizeChanged += (_, _) => FitRenameToolbarLater();
        RenameToolbarTrailing.SizeChanged += (_, _) => FitRenameToolbarLater();
        RenameToolbarCenter.Loaded += (_, _) => FitRenameToolbarLater();

        LocalizeRename();
    }

    private const double FilterWidth = 128, SortWidth = 138, NarrowestField = 76;

    private bool _toolbarFitQueued;

    private void FitRenameToolbarLater()
    {
        if (_toolbarFitQueued) return;
        _toolbarFitQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FitRenameToolbar);
    }

    private void FitRenameToolbar()
    {
        _toolbarFitQueued = false;
        if (_currentPage != "Rename" || !RenameToolbarCenter.IsLoaded) return;

        // Quy về mức thiếu khi hai ô đang ở độ rộng mong muốn, nên kết quả không phụ thuộc độ rộng hiện tại.
        var shortfall = Toolbar.CenterShortfall + (FilterWidth - PreviewSearchBox.Width) + (SortWidth - SortCombo.Width);
        var widths = ToolbarFit.Shrink(shortfall, (FilterWidth, NarrowestField), (SortWidth, NarrowestField));
        if (Math.Abs(PreviewSearchBox.Width - widths[0]) > 0.5) PreviewSearchBox.Width = widths[0];
        if (Math.Abs(SortCombo.Width - widths[1]) > 0.5) SortCombo.Width = widths[1];
    }

    private void LocalizeRename()
    {
        var index = InspectorTabs.SelectedIndex;
        InspectorTabs.Items = $"{Loc.T("MacTabRules")}|{Loc.T("MacTabDetails")}";
        InspectorTabs.SelectedIndex = index;
        TraceEmptyText.Text = Loc.T("MacTraceEmpty");
        PreviewSearchBox.Tag = Loc.T("MacSearchFiles");
        AddMenuButton.ToolTip = Loc.T("MacRenameAdd");
        System.Windows.Automation.AutomationProperties.SetName(AddMenuButton, Loc.T("MacRenameAdd"));
        System.Windows.Automation.AutomationProperties.SetName(DismissBannerButton, Loc.T("MacDismiss"));
        DismissBannerButton.ToolTip = Loc.T("MacDismiss");
    }

    private void ShowInspectorTab(int previous)
    {
        var details = InspectorTabs.SelectedIndex == 1;
        RulesPanel.Visibility = details ? Visibility.Collapsed : Visibility.Visible;
        RulesFooter.Visibility = details ? Visibility.Collapsed : Visibility.Visible;
        TracePanel.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
        SyncTraceEmptyText();
        Motion.Enter(details ? TracePanel : RulesPanel, InspectorTabs.SelectedIndex > previous ? 18 : -18, 0);
    }

    private void SyncTraceEmptyText() =>
        TraceEmptyText.Visibility = _vm.SelectedRow is null ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- Menu "+"

    private ContextMenu NewMenu() => new() { Style = (Style)FindResource("Mac.ContextMenu") };

    private static void OpenBelow(ContextMenu menu, object sender)
    {
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void AddMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu();

        MenuItem Command(string text, RoutedEventHandler click, string? gesture = null)
        {
            var item = new MenuItem { Header = text, InputGestureText = gesture ?? string.Empty };
            item.Click += click;
            return item;
        }

        MenuItem Option(string text, bool value, Action<bool> set, string automationName)
        {
            var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = value };
            item.Click += (_, _) => set(item.IsChecked);
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, automationName);
            return item;
        }

        menu.Items.Add(Command(Loc.T("AddFilesButton"), AddFiles_Click, "Ctrl+O"));
        menu.Items.Add(Command(Loc.T("AddFolderButton"), AddFolder_Click, "Ctrl+Shift+O"));
        menu.Items.Add(Command(Loc.T("ShelfButton"), Shelf_Click, "Ctrl+Shift+S"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Option(Loc.T("RecursiveChip"), _vm.IsRecursive, v => _vm.IsRecursive = v, "AddMenu.Recursive"));
        menu.Items.Add(Option(Loc.T("IncludeFoldersChip"), _vm.IncludeFolders, v => _vm.IncludeFolders = v, "AddMenu.Folders"));
        OpenBelow(menu, sender);
    }

    // ---------------------------------------------------------------- Áp dụng (M10)

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (SheetHost.IsOpen || !_vm.CanApply) return;

        var summary = _vm.BuildApplySummary();
        while (summary.FileCount > 0)
        {
            ActivateAndForeground();
            var confirmed = await SheetHost.ShowAsync(
                ApplyConfirmContent.Title(summary),
                ApplyConfirmContent.Build(summary),
                Loc.T("ConfirmApplyButton"),
                Loc.T("ConfirmCancel"));
            if (!confirmed || !_vm.CanApply) return;

            // Danh sách có thể đổi trong lúc đang hỏi (tệp gửi từ Explorer, lượt xem trước đang chạy):
            // chỉ áp dụng đúng cái người dùng đã đồng ý, khác đi thì hỏi lại.
            var current = _vm.BuildApplySummary();
            if (SameSummary(summary, current)) break;
            summary = current;
        }

        if (summary.FileCount == 0) return;

        await _vm.ApplyAsync();
        CheckAndPromptStoreRating();
    }

    private static bool SameSummary(ApplySummary a, ApplySummary b) =>
        a.FileCount == b.FileCount && a.FolderCount == b.FolderCount && a.ExtensionChangeCount == b.ExtensionChangeCount
        && a.Samples.SequenceEqual(b.Samples);

    // ---------------------------------------------------------------- Thông báo thành công (M12)

    private void SyncSuccessBanner()
    {
        if (!_vm.HasSuccessMessage)
        {
            HideSuccessBanner();
            return;
        }

        SuccessBanner.Visibility = Visibility.Visible;
        Motion.Enter(SuccessBanner, 40, 0, MotionKind.Emphasized);
        SuccessCheck.Play();
        _bannerTimer?.Stop();
        _bannerTimer?.Start();
    }

    private void HideSuccessBanner()
    {
        _bannerTimer?.Stop();
        SuccessBanner.Visibility = Visibility.Collapsed;
    }

    private void DismissBanner_Click(object sender, RoutedEventArgs e) => HideSuccessBanner();

    // ---------------------------------------------------------------- Kéo thả sắp xếp quy tắc (M7)

    private int _dragSource = -1;
    private int _dragTarget = -1;

    private List<FrameworkElement> RuleContainers() => Enumerable.Range(0, RulesItems.Items.Count)
        .Select(i => RulesItems.ItemContainerGenerator.ContainerFromIndex(i))
        .OfType<FrameworkElement>()
        .ToList();

    private void RuleList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(RuleCardViewModel)) is not RuleCardViewModel dragged)
        {
            e.Effects = DragDropEffects.None;
            return;   // kéo tệp từ Explorer: để cửa sổ xử lý
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        var containers = RuleContainers();
        // ActualHeight là chiều cao bố trí, không bị ảnh hưởng bởi phần đang dịch chuyển.
        var heights = containers.Select(c => c.ActualHeight).ToList();
        var source = _vm.Rules.IndexOf(dragged);
        var target = ReorderMath.TargetIndex(e.GetPosition(RulesItems).Y, heights);
        if (source < 0 || source >= heights.Count || (source == _dragSource && target == _dragTarget)) return;

        _dragSource = source;
        _dragTarget = target;
        for (var i = 0; i < containers.Count; i++)
            Motion.To(Motion.RigOf(containers[i]).Move, TranslateTransform.YProperty,
                ReorderMath.Offset(i, source, target, heights[source]), MotionKind.Standard);
    }

    private void RuleList_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave cũng nổi lên khi đi qua phần tử con: chỉ trả về chỗ cũ khi đã ra khỏi cả danh sách.
        var p = e.GetPosition(RulesItems);
        if (p.X <= 0 || p.Y <= 0 || p.X >= RulesItems.ActualWidth || p.Y >= RulesItems.ActualHeight) ResetRuleOffsets(animate: true);
    }

    private void RuleList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(RuleCardViewModel)) is not RuleCardViewModel dragged) return;
        e.Handled = true;

        var heights = RuleContainers().Select(c => c.ActualHeight).ToList();
        var target = ReorderMath.TargetIndex(e.GetPosition(RulesItems).Y, heights);
        ResetRuleOffsets(animate: false);
        if (target >= 0 && target != _vm.Rules.IndexOf(dragged)) _vm.MoveRuleTo(dragged, target);
    }

    private void ResetRuleOffsets(bool animate)
    {
        _dragSource = _dragTarget = -1;
        foreach (var container in RuleContainers())
        {
            var move = Motion.RigOf(container).Move;
            if (animate) Motion.To(move, TranslateTransform.YProperty, 0, MotionKind.Standard);
            else
            {
                move.BeginAnimation(TranslateTransform.YProperty, null);
                move.Y = 0;
            }
        }
    }
}
