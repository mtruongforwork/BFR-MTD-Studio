namespace BFR.Presentation.Design;

using System.Collections.Generic;

/// <summary>
/// Ánh xạ khoá màu cũ (Theme.*.xaml và Themes/Bfr) sang khoá "Mac.*", để các màn hình chưa viết lại
/// dùng được bảng màu mới trong lúc chuyển đổi. Chỉ dùng trong một phiên bản rồi xoá.
/// </summary>
public static class MacLegacyAliasMap
{
    /// <summary>Khoá cọ (Brush) cũ → khoá cọ Mac.</summary>
    public static IReadOnlyDictionary<string, string> Brushes { get; } = new Dictionary<string, string>
    {
        // Bộ cũ (Theme.Light/Dark)
        ["AppBackground"] = "Mac.ContentBg",
        ["PanelBackground"] = "Mac.PaneBg",
        ["SurfaceBackground"] = "Mac.GroupBg",
        ["CardBackground"] = "Mac.GroupBg",
        ["SurfaceHover"] = "Mac.RowHover",
        ["SurfaceSelected"] = "Mac.AccentSoft",
        ["TextPrimary"] = "Mac.Label",
        ["TextSecondary"] = "Mac.Label2",
        ["TextDisabled"] = "Mac.Label3",
        ["TextOnAccent"] = "Mac.OnAccent",
        ["Accent"] = "Mac.AccentText",
        ["AccentHover"] = "Mac.AccentText",
        ["AccentPressed"] = "Mac.AccentFill",
        ["AccentSoft"] = "Mac.AccentSoft",
        ["AccentBorder"] = "Mac.FocusRing",
        ["Border"] = "Mac.Separator",
        ["BorderStrong"] = "Mac.ControlEdge",
        ["Divider"] = "Mac.Separator",
        ["Success"] = "Mac.Ok",
        ["SuccessSoft"] = "Mac.OkSoft",
        ["Warning"] = "Mac.Warn",
        ["WarningSoft"] = "Mac.WarnSoft",
        ["Error"] = "Mac.Bad",
        ["ErrorSoft"] = "Mac.BadSoft",
        ["Info"] = "Mac.AccentText",
        ["InfoSoft"] = "Mac.AccentSoft",
        ["ChipBackground"] = "Mac.Track",
        ["ChipBorder"] = "Mac.Separator",
        ["ChipSelectedBackground"] = "Mac.AccentSoft",
        ["ChipSelectedBorder"] = "Mac.Accent",
        ["DiffAdded"] = "Mac.DiffAddBg",
        ["DiffRemoved"] = "Mac.DiffDelBg",
        ["DiffAddedText"] = "Mac.Ok",
        ["DiffRemovedText"] = "Mac.Bad",

        // Nút Primary của WPF-UI
        ["AccentFillColorDefaultBrush"] = "Mac.AccentFill",
        ["AccentFillColorSecondaryBrush"] = "Mac.AccentFill",
        ["AccentFillColorTertiaryBrush"] = "Mac.AccentFill",
        ["TextOnAccentFillColorPrimaryBrush"] = "Mac.OnAccent",
        ["TextOnAccentFillColorSecondaryBrush"] = "Mac.OnAccent",

        // Bộ Bfr (v2.x)
        ["BfrBgBase"] = "Mac.ContentBg",
        ["BfrSurface1"] = "Mac.PaneBg",
        ["BfrSurface2"] = "Mac.GroupBg",
        ["BfrGlass"] = "Mac.GroupBg",
        ["BfrGlassFill"] = "Mac.GroupBg",
        ["BfrGlassFillStrong"] = "Mac.PaneBg",
        ["BfrGlassHoverFill"] = "Mac.RowHover",
        ["BfrSidebarFill"] = "Mac.SidebarSolid",
        ["BfrInspectorFill"] = "Mac.PaneBg",
        ["BfrLine"] = "Mac.Separator",
        ["BfrHoverRim"] = "Mac.ControlEdge",
        ["BfrFg"] = "Mac.Label",
        ["BfrFg2"] = "Mac.Label2",
        ["BfrFg3"] = "Mac.Label3",
        ["BfrOk"] = "Mac.Ok",
        ["BfrWarn"] = "Mac.Warn",
        ["BfrBad"] = "Mac.Bad",
        ["BfrOkSoft"] = "Mac.OkSoft",
        ["BfrWarnSoft"] = "Mac.WarnSoft",
        ["BfrBadSoft"] = "Mac.BadSoft",
        ["BfrOnAccent"] = "Mac.OnAccent",
        ["BfrAccFg"] = "Mac.AccentText",
        ["BfrAcc1"] = "Mac.AccentFill",
        ["BfrAcc2"] = "Mac.AccentFill",
        ["BfrAccGradient"] = "Mac.AccentFill",
        ["BfrAccGradientSoft"] = "Mac.AccentSoft"
    };

    /// <summary>Khoá phông cũ → khoá phông Mac (bỏ Plus Jakarta Sans khỏi giao diện).</summary>
    public static IReadOnlyDictionary<string, string> Fonts { get; } = new Dictionary<string, string>
    {
        ["BfrFontHeading"] = "Mac.FontSemi",
        ["BfrFontHeadingBold"] = "Mac.FontSemi",
        ["BfrFontDisplay"] = "Mac.FontSemi"
    };
}
