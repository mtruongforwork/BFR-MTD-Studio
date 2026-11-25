using System.Windows;

namespace BFR.App.Controls;

/// <summary>
/// Thẻ Bento chuẩn macOS (bo góc 22px, viền hairline 1px, đổ bóng ngoài, hover nâng -2px khi IsInteractive=True).
/// </summary>
public class BentoCard : GlassTile
{
    public BentoCard()
    {
        SetResourceReference(StyleProperty, typeof(GlassTile));
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(14, 12, 14, 12);
    }
}
