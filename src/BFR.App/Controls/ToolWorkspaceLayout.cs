using System.Windows;
using System.Windows.Controls;

namespace BFR.App.Controls;

/// <summary>
/// Khung 2-Pane Bento Workspace chuẩn macOS cho mọi công cụ:
/// - Cột trái (2* / ~67%): Stage / Preview Bento Card (thanh công cụ nhỏ + nội dung xem trước).
/// - Cột phải (1* / ~33%): Inspector &amp; Action Bento Card (tuỳ chọn cuộn + thanh tóm tắt &amp; CTA cố định đáy).
/// </summary>
public sealed class ToolWorkspaceLayout : Grid
{
    public BentoCard StageCard { get; }
    public BentoCard InspectorCard { get; }

    private readonly ContentPresenter _stageHeaderHost = new();
    private readonly ContentPresenter _stageBodyHost = new();
    private readonly ContentPresenter _inspectorBodyHost = new();
    private readonly ContentPresenter _inspectorFooterHost = new();

    public UIElement? StageHeader
    {
        get => _stageHeaderHost.Content as UIElement;
        set
        {
            _stageHeaderHost.Content = value;
            _stageHeaderHost.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public UIElement? StageContent
    {
        get => _stageBodyHost.Content as UIElement;
        set => _stageBodyHost.Content = value;
    }

    public UIElement? InspectorContent
    {
        get => _inspectorBodyHost.Content as UIElement;
        set => _inspectorBodyHost.Content = value;
    }

    public UIElement? InspectorFooter
    {
        get => _inspectorFooterHost.Content as UIElement;
        set
        {
            _inspectorFooterHost.Content = value;
            _inspectorFooterHost.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public ToolWorkspaceLayout()
    {
        Margin = new Thickness(18, 14, 0, 0);

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 440 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 260 });

        // Left: Stage / Preview Card
        var stageDock = new DockPanel();
        DockPanel.SetDock(_stageHeaderHost, Dock.Top);
        _stageHeaderHost.Margin = new Thickness(0, 0, 0, 12);
        _stageHeaderHost.Visibility = Visibility.Collapsed;
        stageDock.Children.Add(_stageHeaderHost);
        stageDock.Children.Add(_stageBodyHost);

        StageCard = new BentoCard
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 16),
            Content = stageDock
        };
        SetColumn(StageCard, 0);
        Children.Add(StageCard);

        // Right: macOS Inspector (#252525 + 1px left divider)
        var inspectorDock = new DockPanel();
        DockPanel.SetDock(_inspectorFooterHost, Dock.Bottom);
        _inspectorFooterHost.Margin = new Thickness(0, 12, 0, 0);
        _inspectorFooterHost.Visibility = Visibility.Collapsed;
        inspectorDock.Children.Add(_inspectorFooterHost);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _inspectorBodyHost
        };
        inspectorDock.Children.Add(scroll);

        InspectorCard = new BentoCard
        {
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(1, 1, 0, 0),
            Padding = new Thickness(16),
            Content = inspectorDock
        };
        InspectorCard.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "BfrInspectorFill");
        SetColumn(InspectorCard, 2);
        Children.Add(InspectorCard);
    }
}
