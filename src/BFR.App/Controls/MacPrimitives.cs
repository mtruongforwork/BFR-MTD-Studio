using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BFR.App.Controls;

/// <summary>
/// macOS HIG Push Button (28px regular / 22px small, 6px corner radius).
/// </summary>
public sealed class MacButton : Button
{
    public enum Variant { Primary, Secondary, Destructive, Subtle }

    public MacButton(string text, Variant variant = Variant.Secondary, bool small = false)
    {
        Content = text;
        Height = small ? 22 : 28;
        Padding = small ? new Thickness(8, 0, 8, 0) : new Thickness(12, 0, 12, 0);
        FontSize = small ? 11.5 : 12.5;
        Cursor = Cursors.Hand;
        VerticalContentAlignment = VerticalAlignment.Center;
        HorizontalContentAlignment = HorizontalAlignment.Center;

        switch (variant)
        {
            case Variant.Primary:
                SetResourceReference(StyleProperty, "BfrPrimaryButton");
                break;
            case Variant.Destructive:
                SetResourceReference(StyleProperty, "BfrSecondaryButton");
                SetResourceReference(ForegroundProperty, "BfrBad");
                break;
            case Variant.Subtle:
                SetResourceReference(StyleProperty, "BfrGhostButton");
                break;
            default:
                SetResourceReference(StyleProperty, "BfrSecondaryButton");
                break;
        }
    }
}

/// <summary>
/// macOS HIG Segmented Control (3px outer padding, 6px segments, subtle active pill).
/// </summary>
public sealed class MacSegmentedControl : Border
{
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal };
    private readonly List<(string Key, RadioButton Btn)> _items = new();
    private bool _suppress;

    public event Action<string>? SelectionChanged;

    public MacSegmentedControl(IEnumerable<(string Key, string Label)> segments, string? initialKey = null)
    {
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(3);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "BfrGlassFill");
        SetResourceReference(BorderBrushProperty, "BfrLine");
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        Child = _panel;

        var group = "MacSeg_" + Guid.NewGuid().ToString("N");
        var first = true;
        foreach (var (key, label) in segments)
        {
            var k = key;
            var rb = new RadioButton
            {
                Content = label,
                GroupName = group,
                IsChecked = initialKey is not null ? string.Equals(key, initialKey, StringComparison.OrdinalIgnoreCase) : first,
                Cursor = Cursors.Hand,
                Padding = new Thickness(11, 4, 11, 4),
                FontSize = 12
            };
            rb.SetResourceReference(StyleProperty, "SegmentedRadioButtonStyle");
            rb.Checked += (_, _) =>
            {
                if (!_suppress) SelectionChanged?.Invoke(k);
            };
            _items.Add((key, rb));
            _panel.Children.Add(rb);
            first = false;
        }
    }

    public void SetSelected(string key)
    {
        _suppress = true;
        try
        {
            foreach (var (k, btn) in _items)
                btn.IsChecked = string.Equals(k, key, StringComparison.OrdinalIgnoreCase);
        }
        finally { _suppress = false; }
    }
}

/// <summary>
/// macOS HIG NSBox Group Container (10px radius, subtle 4.5% fill, 1px hairline border, uppercase 11px title).
/// </summary>
public sealed class MacInspectorBox : StackPanel
{
    public StackPanel Body { get; } = new();

    public MacInspectorBox(string? title = null)
    {
        Margin = new Thickness(0, 0, 0, 12);

        if (!string.IsNullOrWhiteSpace(title))
        {
            var header = new TextBlock
            {
                Text = title.ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 0, 6)
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "BfrFg3");
            Children.Add(header);
        }

        var box = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            Child = Body
        };
        box.SetResourceReference(Border.BackgroundProperty, "BfrGlassFill");
        box.SetResourceReference(Border.BorderBrushProperty, "BfrLine");
        Children.Add(box);
    }
}

/// <summary>
/// macOS HIG Pill Switch (green/accent when ON, compact 36x20).
/// </summary>
public sealed class MacSwitch : ToggleButton
{
    public MacSwitch(bool isChecked = false)
    {
        IsChecked = isChecked;
        Width = 36;
        Height = 20;
        Cursor = Cursors.Hand;
        Focusable = true;

        var bd = new FrameworkElementFactory(typeof(Border), "Track");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        bd.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)));

        var thumb = new FrameworkElementFactory(typeof(Border), "Thumb");
        thumb.SetValue(WidthProperty, 16.0);
        thumb.SetValue(HeightProperty, 16.0);
        thumb.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        thumb.SetValue(Border.BackgroundProperty, Brushes.White);
        thumb.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        thumb.SetValue(MarginProperty, new Thickness(2));
        bd.AppendChild(thumb);

        var tpl = new ControlTemplate(typeof(ToggleButton)) { VisualTree = bd };
        var onTrigger = new Trigger { Property = IsCheckedProperty, Value = true };
        onTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)), "Track"));
        onTrigger.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Right, "Thumb"));
        tpl.Triggers.Add(onTrigger);

        Template = tpl;
    }
}
