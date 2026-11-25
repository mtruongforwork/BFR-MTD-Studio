namespace BFR.App.Controls.Mac;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using BFR.App.Infrastructure;

/// <summary>
/// Popover kiểu macOS (M11): khung nổi có mũi tên chỉ về nút neo, phóng ra từ điểm neo.
/// Bấm ra ngoài hoặc Esc thì đóng, tiêu điểm trở về nút neo.
/// </summary>
public sealed class MacPopover : Popup
{
    private readonly Grid _root = new() { Margin = new Thickness(12, 0, 12, 14) };

    public MacPopover(FrameworkElement anchor, UIElement content, string name)
    {
        PlacementTarget = anchor;
        Placement = PlacementMode.Bottom;
        HorizontalOffset = -12;
        VerticalOffset = 2;
        AllowsTransparency = true;
        StaysOpen = false;

        var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 7, 0, 0), Child = content };
        card.SetResourceReference(Border.BackgroundProperty, "Mac.MenuBg");
        card.SetResourceReference(Border.BorderBrushProperty, "Mac.Separator");
        card.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 5, Direction = 270, Opacity = 0.3 };

        // Mũi tên: tam giác đè lên viền trên của khung để hai phần liền nhau.
        var arrow = new Path
        {
            Data = Geometry.Parse("M0,8 L8,0 L16,8"),
            Width = 16,
            Height = 8,
            Margin = new Thickness(22, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            StrokeThickness = 1
        };
        arrow.SetResourceReference(Shape.FillProperty, "Mac.MenuBg");
        arrow.SetResourceReference(Shape.StrokeProperty, "Mac.Separator");

        _root.Children.Add(card);
        _root.Children.Add(arrow);
        _root.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "Mac.Font");
        AutomationProperties.SetName(card, name);
        Child = _root;

        Opened += (_, _) => Motion.ScaleIn(_root, new Point(0.15, 0), 0.96);
        _root.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            IsOpen = false;
            anchor.Focus();
        };
    }
}
