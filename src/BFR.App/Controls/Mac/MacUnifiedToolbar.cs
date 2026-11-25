namespace BFR.App.Controls.Mac;

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BFR.App.Infrastructure;

/// <summary>
/// Toolbar hợp nhất 52 px: ẩn/hiện thanh bên, lùi/tiến, tiêu đề, điều khiển riêng của trang, ô tìm lệnh và nút inspector.
/// Kéo vào nền để di chuyển cửa sổ.
/// </summary>
public sealed class MacUnifiedToolbar : Border
{
    /// <summary>Chỗ chừa cho traffic light khi thanh bên đã thu.</summary>
    public const double TrafficLightsWidth = 66;

    private readonly Border _lightsSpacer = new() { Width = 0 };
    private readonly Button _sidebar;
    private readonly Button _back;
    private readonly Button _forward;
    private readonly Button _inspector;
    private readonly Button _search = new();
    private readonly TextBlock _searchLabel = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 16, 0), MaxWidth = 320 };
    private readonly ContentPresenter _center = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentPresenter _trailing = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

    public MacUnifiedToolbar()
    {
        Height = 52;
        BorderThickness = new Thickness(0, 0, 0, 1);
        SetResourceReference(BorderBrushProperty, "Mac.Separator");
        SetResourceReference(BackgroundProperty, "Mac.ToolbarBg");

        _sidebar = ToolbarButton("PanelLeft", 17, () => SidebarToggleRequested?.Invoke());
        _back = ToolbarButton("ChevronLeft", 17, () => BackRequested?.Invoke());
        _forward = ToolbarButton("ChevronRight", 17, () => ForwardRequested?.Invoke());
        _inspector = ToolbarButton("PanelRight", 17, () => InspectorToggleRequested?.Invoke());
        _inspector.Visibility = Visibility.Collapsed;

        _title.SetResourceReference(StyleProperty, "Mac.Title2");
        AutomationProperties.SetAutomationId(_title, "Shell.Title");

        var dock = new DockPanel { Margin = new Thickness(10, 0, 12, 0) };
        void Add(UIElement element, Dock side)
        {
            DockPanel.SetDock(element, side);
            dock.Children.Add(element);
        }

        Add(_lightsSpacer, Dock.Left);
        Add(_sidebar, Dock.Left);
        Add(_back, Dock.Left);
        Add(_forward, Dock.Left);
        Add(_title, Dock.Left);
        Add(_inspector, Dock.Right);
        Add(BuildSearch(), Dock.Right);
        Add(_trailing, Dock.Right);
        dock.Children.Add(_center);
        Child = dock;
    }

    public event Action? SidebarToggleRequested;

    public event Action? BackRequested;

    public event Action? ForwardRequested;

    public event Action? SearchRequested;

    public event Action? InspectorToggleRequested;

    public string Title => _title.Text;

    private static Button ToolbarButton(string icon, double size, Action click)
    {
        var glyph = new MacIcon { Size = size };
        glyph.SetResourceReference(MacIcon.DataProperty, "Icon." + icon);
        var button = new Button { Content = glyph };
        button.SetResourceReference(StyleProperty, "Mac.ToolbarButton");
        button.Click += (_, _) => click();
        return button;
    }

    private Button BuildSearch()
    {
        _search.SetResourceReference(StyleProperty, "Mac.SearchTrigger");
        _search.Width = 200;
        _search.Margin = new Thickness(0, 0, 6, 0);

        var row = new DockPanel();
        var shortcut = new TextBlock { Text = "Ctrl K", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        shortcut.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label3");
        DockPanel.SetDock(shortcut, Dock.Right);
        row.Children.Add(shortcut);

        var glyph = new MacIcon { Size = 13, Margin = new Thickness(0, 0, 7, 0) };
        glyph.SetResourceReference(MacIcon.DataProperty, "Icon.Search");
        glyph.SetResourceReference(MacIcon.ForegroundProperty, "Mac.Label3");
        DockPanel.SetDock(glyph, Dock.Left);
        row.Children.Add(glyph);

        _searchLabel.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label3");
        row.Children.Add(_searchLabel);

        _search.Content = row;
        _search.Click += (_, _) => SearchRequested?.Invoke();
        return _search;
    }

    public void Localize(Func<string, string> text)
    {
        void Name(Button button, string key, string? shortcut = null)
        {
            button.ToolTip = shortcut is null ? text(key) : $"{text(key)} ({shortcut})";
            AutomationProperties.SetName(button, text(key));
        }

        Name(_sidebar, "MacToolbarSidebar", "Ctrl+Alt+S");
        Name(_back, "MacToolbarBack", "Alt+←");
        Name(_forward, "MacToolbarForward", "Alt+→");
        Name(_inspector, "MacToolbarInspector", "Ctrl+Alt+I");
        Name(_search, "MacToolbarSearch", "Ctrl+K");
        _searchLabel.Text = text("MacToolbarSearch");
        AutomationProperties.SetAutomationId(_sidebar, "Shell.ToggleSidebar");
        AutomationProperties.SetAutomationId(_inspector, "Shell.ToggleInspector");
    }

    public void SetTitle(string title)
    {
        if (_title.Text == title) return;
        _title.Text = title;
        if (IsLoaded) Motion.FadeIn(_title, BFR.Presentation.Design.MotionKind.Standard);
    }

    public void SetHistory(bool canGoBack, bool canGoForward)
    {
        _back.IsEnabled = canGoBack;
        _forward.IsEnabled = canGoForward;
    }

    /// <summary>Gắn điều khiển riêng của trang; null để bỏ.</summary>
    public void SetPageItems(FrameworkElement? center, FrameworkElement? trailing)
    {
        Swap(_center, center);
        Swap(_trailing, trailing);
    }

    private static void Swap(ContentPresenter host, FrameworkElement? content)
    {
        if (ReferenceEquals(host.Content, content)) return;
        host.Content = content;
        if (content is not null && host.IsLoaded) Motion.FadeIn(host, BFR.Presentation.Design.MotionKind.Standard);
    }

    public void SetInspectorAvailable(bool available) => _inspector.Visibility = available ? Visibility.Visible : Visibility.Collapsed;

    public void SetSidebarVisible(bool visible, bool animate)
    {
        var width = visible ? 0 : TrafficLightsWidth;
        if (animate) Motion.AnimateWidth(_lightsSpacer, width);
        else _lightsSpacer.Width = width;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Window.GetWindow(this) is { } window) WindowDrag.Handle(window, e);
    }
}
