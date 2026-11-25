namespace BFR.App.Infrastructure;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

/// <summary>
/// Popup button kiểu macOS: danh sách bật ra sao cho mục đang chọn nằm đè đúng lên nút.
/// Danh sách dài phải cuộn (hoặc chưa chọn gì) thì bật ra bên dưới như ComboBox thường.
/// Các hằng số khớp với template Mac.ComboBox.
/// </summary>
public static class MacPopup
{
    private const double RowHeight = 24;

    /// <summary>Khoảng từ mép trên popup tới mục đầu: lề đổ bóng 12 + viền 1 + đệm 5.</summary>
    private const double TopInset = 18;

    /// <summary>Lệch ngang để chữ trong danh sách thẳng cột với chữ trên nút (chừa chỗ cho dấu chọn).</summary>
    private const double ShiftX = -33;

    /// <summary>Số mục tối đa vẫn căn đè được (MaxHeight 280 / 24).</summary>
    private const int MaxAlignedItems = 11;

    public static readonly DependencyProperty AlignSelectionProperty = DependencyProperty.RegisterAttached(
        "AlignSelection", typeof(bool), typeof(MacPopup), new PropertyMetadata(false, OnAlignSelectionChanged));

    public static bool GetAlignSelection(DependencyObject o) => (bool)o.GetValue(AlignSelectionProperty);

    public static void SetAlignSelection(DependencyObject o, bool value) => o.SetValue(AlignSelectionProperty, value);

    private static void OnAlignSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Popup popup || e.NewValue is not true) return;

        popup.Placement = PlacementMode.Custom;
        popup.CustomPopupPlacementCallback = (_, targetSize, _) =>
        {
            var combo = popup.TemplatedParent as ComboBox;
            // Kích thước truyền vào tính theo pixel thiết bị: quy đổi theo DPI từ chiều cao thật của nút.
            var scale = combo is { ActualHeight: > 0 } ? targetSize.Height / combo.ActualHeight : 1;
            var index = combo?.SelectedIndex ?? -1;
            var below = new CustomPopupPlacement(new Point(-12 * scale, targetSize.Height - (8 * scale)), PopupPrimaryAxis.Horizontal);

            if (combo is null || index < 0 || combo.Items.Count > MaxAlignedItems) return new[] { below };

            var y = (targetSize.Height - (RowHeight * scale)) / 2 - ((TopInset + (RowHeight * index)) * scale);
            return new[] { new CustomPopupPlacement(new Point(ShiftX * scale, y), PopupPrimaryAxis.Horizontal), below };
        };
    }
}
