namespace BFR.App;

using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BFR.App.Infrastructure;
using BFR.Presentation;

/// <summary>
/// Command Palette (Ctrl+K): gõ để tìm lệnh (hỗ trợ IME tiếng Việt/Trung), ↑↓ hoặc bấm chuột để chọn &amp; chạy,
/// tự đóng khi mất tiêu điểm (click ra ngoài) hoặc nhấn Esc.
/// </summary>
public partial class CommandPaletteWindow : Window
{
    private readonly CommandPaletteViewModel _vm;
    private PaletteCommand? _mouseDownCommand;
    private bool _closing;

    public CommandPaletteWindow(CommandPaletteViewModel vm, Window? owner = null)
    {
        _vm = vm;

        InitializeComponent();
        DataContext = vm;
        var grouped = new ListCollectionView(vm.Filtered);
        grouped.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PaletteCommand.Category)));
        ResultsList.ItemsSource = grouped;

        if (owner is not null) Owner = owner;
        PlaceLikeSpotlight(owner);

        InputBindings.Add(new KeyBinding(new RelayCommand(SafeClose), Key.Escape, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MoveAndScroll(-1)), Key.Up, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => MoveAndScroll(1)), Key.Down, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(RunSelected), Key.Enter, ModifierKeys.None));

        Deactivated += (_, _) => SafeClose();

        Loaded += (_, _) =>
        {
            if (InputBox != null)
            {
                InputBox.Focus();
                InputBox.SelectAll();
            }

            // M16: phóng nhẹ từ 0,98 (Motion tự bỏ phóng khi giảm chuyển động).
            Motion.ScaleIn(RootCard, new Point(0.5, 0.3), 0.98);
        };
    }

    /// <summary>Giữa theo chiều ngang, ở khoảng một phần năm từ trên xuống của cửa sổ chủ (hoặc màn hình).</summary>
    private void PlaceLikeSpotlight(Window? owner)
    {
        var area = owner is { IsVisible: true, WindowState: WindowState.Normal }
            ? new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight)
            : SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + Math.Max(24, (area.Height - Height) / 5);
    }

    private void MoveAndScroll(int delta)
    {
        _vm.MoveSelection(delta);
        if (_vm.Selected is not null) ResultsList?.ScrollIntoView(_vm.Selected);
    }

    private void RunSelected()
    {
        var selected = _vm.Selected;
        if (selected is null) return;
        SafeClose();
        _vm.Execute(selected);
    }

    private void RunCommand(PaletteCommand? command)
    {
        if (command is null) return;
        SafeClose();
        _vm.Execute(command);
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); } catch (InvalidOperationException) { /* đang đóng */ }
    }

    private static PaletteCommand? FindCommandFromSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListBoxItem item && item.DataContext is PaletteCommand cmd) return cmd;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void ResultsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownCommand = FindCommandFromSource(e.OriginalSource as DependencyObject);
    }

    private void ResultsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var released = FindCommandFromSource(e.OriginalSource as DependencyObject);
        if (released is not null && ReferenceEquals(released, _mouseDownCommand))
        {
            _mouseDownCommand = null;
            e.Handled = true;
            RunCommand(released);
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var cmd = FindCommandFromSource(e.OriginalSource as DependencyObject) ?? _vm.Selected;
        if (cmd is not null)
        {
            e.Handled = true;
            RunCommand(cmd);
        }
    }
}

/// <summary>Tách chuỗi phím tắt (VD: "Ctrl+Shift+O") thành từng phím riêng để vẽ keycap &lt;kbd&gt;.</summary>
internal sealed class ShortcutKeycapsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        return s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

internal sealed class StringNotEmptyToVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
