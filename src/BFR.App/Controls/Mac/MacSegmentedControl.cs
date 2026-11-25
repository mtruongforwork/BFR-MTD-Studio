namespace BFR.App.Controls.Mac;

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BFR.App.Infrastructure;
using BFR.Presentation.Design;

/// <summary>
/// Segmented control kiểu AppKit: rãnh xám, núm sáng trượt theo lò xo. Các ô rộng bằng nhau.
/// Bên trong là RadioButton nên bàn phím và Narrator dùng được như nhóm lựa chọn thường.
/// </summary>
public sealed class MacSegmentedControl : Grid
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(string), typeof(MacSegmentedControl), new PropertyMetadata(string.Empty, (d, _) => ((MacSegmentedControl)d).Rebuild()));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(MacSegmentedControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((MacSegmentedControl)d).OnSelected((int)e.OldValue)));

    private readonly Border _thumb = new() { CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0.5) };
    private readonly TranslateTransform _thumbX = new();
    private readonly UniformGrid _segments = new() { Rows = 1 };
    private readonly string _group = Guid.NewGuid().ToString("N");

    public MacSegmentedControl()
    {
        Height = 26;
        var track = new Border { CornerRadius = new CornerRadius(7) };
        track.SetResourceReference(Border.BackgroundProperty, "Mac.Track");
        _thumb.SetResourceReference(Border.BackgroundProperty, "Mac.Thumb");
        _thumb.SetResourceReference(Border.BorderBrushProperty, "Mac.ControlBorder");
        _thumb.RenderTransform = _thumbX;

        var inner = new Grid { Margin = new Thickness(2) };
        inner.Children.Add(_thumb);
        inner.Children.Add(_segments);
        Children.Add(track);
        Children.Add(inner);

        _segments.SizeChanged += (_, _) => PlaceThumb(animate: false);
    }

    /// <summary>Phát khi người dùng hoặc code đổi lựa chọn; tham số là chỉ số cũ để biết hướng chuyển cảnh.</summary>
    public event Action<int>? SelectionChanged;

    /// <summary>Nhãn các ô, ngăn bằng dấu '|'.</summary>
    public string Items { get => (string)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    private void Rebuild()
    {
        _segments.Children.Clear();
        var labels = Items.Split('|', StringSplitOptions.RemoveEmptyEntries);
        _segments.Columns = Math.Max(1, labels.Length);
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var button = new RadioButton { Content = labels[i], GroupName = _group, IsChecked = i == SelectedIndex };
            button.SetResourceReference(StyleProperty, "Mac.Segment");
            AutomationProperties.SetName(button, labels[i]);
            button.Checked += (_, _) => SelectedIndex = index;
            _segments.Children.Add(button);
        }
    }

    private void OnSelected(int oldIndex)
    {
        if (SelectedIndex >= 0 && SelectedIndex < _segments.Children.Count)
            ((RadioButton)_segments.Children[SelectedIndex]).IsChecked = true;
        PlaceThumb(animate: true);
        SelectionChanged?.Invoke(oldIndex);
    }

    private void PlaceThumb(bool animate)
    {
        if (_segments.ActualWidth <= 0) return;

        var width = _segments.ActualWidth / Math.Max(1, _segments.Columns);
        _thumb.Width = width;
        var target = width * Math.Max(0, SelectedIndex);
        if (animate && IsLoaded)
        {
            Motion.To(_thumbX, TranslateTransform.XProperty, target, MotionKind.Standard);
        }
        else
        {
            _thumbX.BeginAnimation(TranslateTransform.XProperty, null);
            _thumbX.X = target;
        }
    }
}
