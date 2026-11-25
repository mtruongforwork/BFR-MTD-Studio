namespace BFR.App;

using BFR.App.Controls.Mac;
using BFR.Core.Licensing;
using BFR.Presentation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

/// <summary>
/// Trang Shelf (master–detail): cùng <see cref="ShelfModel"/> với cửa sổ Shelf nổi nên tệp thả vào một nơi sẽ hiện ở nơi kia.
/// Danh sách ở vùng nội dung, chi tiết mục đang chọn và nút "Đổi tên…" (chuyển cả khay sang trang Đổi tên) ở inspector.
/// </summary>
public sealed class ShelfPage : PageBase, IMacShellContent
{
    private sealed record Row(string Name, string Folder, string FullPath, bool IsFolder)
    {
        public string IconKey => IsFolder ? "Folder" : "File";
        public override string ToString() => Name;
    }

    private readonly ShelfModel _model;
    private readonly Action<IReadOnlyList<string>> _rename;
    private readonly Action<ProFeature> _onLimitReached;

    private readonly ListBox _list = new();
    private readonly StackPanel _empty;
    private readonly MacDetailPane _detail = new();
    private readonly Button _renameButton;
    private readonly Button _reveal;
    private readonly Button _remove;
    private readonly Button _clear;

    public ShelfPage(ShelfModel model, Action<IReadOnlyList<string>> rename, Action<ProFeature> onLimitReached)
    {
        _model = model;
        _rename = rename;
        _onLimitReached = onLimitReached;

        AcceptDrops(this);
        // Inspector nằm ngoài trang: không nhận thả ở đó thì cửa sổ chính sẽ đưa tệp sang trang Đổi tên.
        AcceptDrops(_detail);
        AutomationProperties.SetName(this, "Shelf page");

        _list.SetResourceReference(StyleProperty, "Mac.ListBox");
        _list.SelectionMode = SelectionMode.Extended;
        _list.ItemTemplate = BuildRowTemplate();
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) => ShowSelection();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Delete) return;
            RemoveSelected();
            e.Handled = true;
        };
        AutomationProperties.SetName(_list, "Shelf items");

        _empty = MacDetailPane.CreateEmptyState("Inbox", Loc.T("MacShelfEmptyTitle"), Loc.T("ShelfHint"));
        _empty.IsHitTestVisible = false;

        var root = new Grid();
        root.Children.Add(_list);
        root.Children.Add(_empty);
        Content = root;

        _clear = MacDetailPane.CreateButton(Loc.T("ShelfClear"));
        _clear.Click += (_, _) => _model.Clear();

        _renameButton = MacDetailPane.CreateButton(Loc.T("ShelfRename"), "Mac.Button.Primary", "Shelf rename all");
        _renameButton.Click += (_, _) =>
        {
            if (_model.IsEmpty) return;
            _rename(_model.Snapshot());
            _model.Clear();
        };
        _detail.Footer.Children.Add(_renameButton);

        _reveal = MacDetailPane.CreateButton(Loc.T("MacShowInExplorer"));
        _reveal.Click += (_, _) => Reveal();
        _remove = MacDetailPane.CreateButton(Loc.T("MacShelfRemove"));
        _remove.Click += (_, _) => RemoveSelected();

        _model.Items.CollectionChanged += OnItemsChanged;
        Refresh();
    }

    FrameworkElement? IMacShellContent.ToolbarCenter => null;

    FrameworkElement? IMacShellContent.ToolbarTrailing => _clear;

    FrameworkElement? IMacShellContent.Inspector => _detail;

    string? IMacShellContent.StatusText => Loc.F("MacFilesCount", _model.Count);

    public event EventHandler? ShellContentChanged;

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Refresh();

    public override void OnShown() => Refresh();

    public override void Shutdown() => _model.Items.CollectionChanged -= OnItemsChanged;

    private void AcceptDrops(FrameworkElement target)
    {
        target.AllowDrop = true;
        target.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        target.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
            e.Handled = true;   // không để cửa sổ chính thêm tệp vào danh sách đổi tên
        };
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var result = _model.Add(paths);
        if (result.HitLimit) _onLimitReached(ProFeature.UnlimitedShelf);
    }

    private void RemoveSelected()
    {
        foreach (var row in _list.SelectedItems.OfType<Row>().ToList()) _model.Remove(row.FullPath);
    }

    private void Reveal()
    {
        if (_list.SelectedItem is not Row row) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.FullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MacAlert.Show(ex.Message, Loc.T("NavShelf"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static DataTemplate BuildRowTemplate() => (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      xmlns:mac="clr-namespace:BFR.App.Controls.Mac;assembly=BatchFileRenamer">
          <DockPanel Margin="0,3">
            <mac:MacIcon x:Name="Glyph" DockPanel.Dock="Left" Size="18" Margin="0,0,10,0" Data="{DynamicResource Icon.File}"
                         Foreground="{Binding RelativeSource={RelativeSource AncestorType=ListBoxItem}, Path=Foreground}" />
            <StackPanel>
              <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis"
                         Foreground="{Binding RelativeSource={RelativeSource AncestorType=ListBoxItem}, Path=Foreground}" />
              <TextBlock Text="{Binding Folder}" FontSize="11.5" Opacity="0.75" TextTrimming="CharacterEllipsis"
                         Foreground="{Binding RelativeSource={RelativeSource AncestorType=ListBoxItem}, Path=Foreground}" />
            </StackPanel>
          </DockPanel>
          <DataTemplate.Triggers>
            <DataTrigger Binding="{Binding IsFolder}" Value="True">
              <Setter TargetName="Glyph" Property="Data" Value="{DynamicResource Icon.Folder}" />
            </DataTrigger>
          </DataTemplate.Triggers>
        </DataTemplate>
        """);

    private void Refresh()
    {
        var selected = _list.SelectedItems.OfType<Row>().Select(r => r.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = _model.Items.Select(p =>
        {
            var trimmed = p.TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            return new Row(name.Length > 0 ? name : p, Path.GetDirectoryName(trimmed) ?? string.Empty, p, Directory.Exists(p));
        }).ToList();
        _list.ItemsSource = rows;
        foreach (var row in rows.Where(r => selected.Contains(r.FullPath))) _list.SelectedItems.Add(row);

        _empty.Visibility = _model.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        _renameButton.IsEnabled = !_model.IsEmpty;
        _clear.IsEnabled = !_model.IsEmpty;
        ShowSelection();
        ShellContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowSelection()
    {
        var rows = _list.SelectedItems.OfType<Row>().ToList();
        if (rows.Count == 0)
        {
            _detail.ShowEmpty(Loc.T(_model.IsEmpty ? "MacShelfEmptyTitle" : "MacShelfSelectHint"), keepFooter: true);
            return;
        }

        if (rows.Count > 1)
        {
            _detail.Show(Loc.F("MacSelectedCount", rows.Count));
        }
        else
        {
            var row = rows[0];
            _detail.Show(row.Name);
            _detail.AddRow(Loc.T("MacDetailFolder"), row.Folder);
            foreach (var (label, value) in Facts(row)) _detail.AddRow(label, value);
            AddAction(_reveal);
        }

        AddAction(_remove);
    }

    private void AddAction(Button button)
    {
        button.Margin = new Thickness(0, 10, 0, 0);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        _detail.Body.Children.Add(button);
    }

    private static IEnumerable<(string, string)> Facts(Row row)
    {
        var facts = new List<(string, string)>();
        try
        {
            FileSystemInfo info = row.IsFolder ? new DirectoryInfo(row.FullPath) : new FileInfo(row.FullPath);
            if (!info.Exists)
            {
                facts.Add((Loc.T("MacDetailState"), Loc.T("HistoryPreviewMissing")));
                return facts;
            }

            if (info is FileInfo file) facts.Add((Loc.T("MacDetailSize"), FormatSize(file.Length)));
            facts.Add((Loc.T("MacDetailModified"), info.LastWriteTime.ToString("g")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            facts.Add((Loc.T("MacDetailState"), ex.Message));
        }

        return facts;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
