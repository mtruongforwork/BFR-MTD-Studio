namespace BFR.App;

using BFR.App.Controls.Mac;
using BFR.Core;
using BFR.Core.Licensing;
using BFR.Presentation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

/// <summary>
/// Thư viện preset (master–detail): danh sách có ô tìm ở vùng nội dung; mô tả, chuỗi quy tắc và các nút
/// Áp dụng / Chia sẻ / Xoá ở inspector; Lưu preset hiện tại và Nhập từ clipboard trên toolbar.
/// Bản Free lưu tối đa 3 preset; Pro lưu không giới hạn (PHẦN 3.6).
/// </summary>
public class PresetsPage : PageBase, IMacShellContent
{
    private readonly PresetStore _store;
    private readonly LicenseService _license;
    private readonly ListBox _list = new();
    private readonly TextBox _search = new();
    private readonly StackPanel _empty;
    private readonly MacDetailPane _detail = new();
    private readonly Button _applyButton;
    private readonly Button _shareButton;
    private readonly Button _deleteButton;
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal };
    private readonly IStringProvider _strings = new LocStringProvider();
    private List<Preset> _all = new();
    private string _status = string.Empty;

    private readonly Func<Preset> _currentProvider;

    /// <summary>Người dùng bấm "Áp dụng" trên một preset: cửa sổ chính nạp preset rồi quay về trang Đổi tên.</summary>
    public event EventHandler<Preset>? PresetApplied;

    /// <summary>Danh sách preset có thể đã đổi (lưu/xoá): cửa sổ chính nạp lại danh sách ở combobox.</summary>
    public event EventHandler? Left;

    public event EventHandler? ShellContentChanged;

    FrameworkElement? IMacShellContent.ToolbarCenter => null;

    FrameworkElement? IMacShellContent.ToolbarTrailing => _toolbar;

    FrameworkElement? IMacShellContent.Inspector => _detail;

    string? IMacShellContent.StatusText => _status;

    public override void OnShown() => Refresh();

    public override void OnHidden() => Left?.Invoke(this, EventArgs.Empty);

    public PresetsPage(PresetStore store, Func<Preset> currentProvider, LicenseService license)
    {
        _store = store;
        _currentProvider = currentProvider;
        _license = license;

        AutomationProperties.SetName(this, "Preset library window");

        var save = MacDetailPane.CreateButton(Loc.T("LibrarySave"));
        save.Click += (_, _) => SaveCurrent(_currentProvider());
        var import = MacDetailPane.CreateButton(Loc.T("ImportFromClipboard"));
        import.Margin = new Thickness(6, 0, 0, 0);
        import.Click += (_, _) => ImportFromClipboard();
        _toolbar.Children.Add(save);
        _toolbar.Children.Add(import);

        _applyButton = MacDetailPane.CreateButton(Loc.T("LibraryApply"), "Mac.Button.Primary");
        _applyButton.Click += (_, _) => ApplySelected();
        _shareButton = MacDetailPane.CreateButton(Loc.T("SharePreset"));
        _shareButton.Margin = new Thickness(0, 6, 0, 0);
        _shareButton.Click += (_, _) => ShareSelected();
        _deleteButton = MacDetailPane.CreateButton(Loc.T("LibraryDelete"), "Mac.Button.Destructive");
        _deleteButton.Margin = new Thickness(0, 6, 0, 0);
        _deleteButton.Click += (_, _) => DeleteSelected();
        _detail.Footer.Children.Add(_applyButton);
        _detail.Footer.Children.Add(_shareButton);
        _detail.Footer.Children.Add(_deleteButton);

        _search.SetResourceReference(StyleProperty, "Mac.SearchBox");
        _search.Tag = Loc.T("PresetLibrarySearch");
        _search.Margin = new Thickness(14, 10, 14, 6);
        _search.TextChanged += (_, _) => ApplyFilter();
        AutomationProperties.SetName(_search, "Search presets");

        _list.SetResourceReference(StyleProperty, "Mac.ListBox");
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Margin="0,3">
                <TextBlock Text="{Binding DisplayName}" TextTrimming="CharacterEllipsis"
                           Foreground="{Binding RelativeSource={RelativeSource AncestorType=ListBoxItem}, Path=Foreground}" />
                <TextBlock Text="{Binding DisplayDescription}" FontSize="11.5" Opacity="0.75" TextTrimming="CharacterEllipsis"
                           Foreground="{Binding RelativeSource={RelativeSource AncestorType=ListBoxItem}, Path=Foreground}" />
              </StackPanel>
            </DataTemplate>
            """);
        _list.SelectionChanged += (_, _) => ShowSelection();
        _list.MouseDoubleClick += (_, _) => ApplySelected();
        AutomationProperties.SetName(_list, "Saved presets");

        _empty = MacDetailPane.CreateEmptyState("Library", Loc.T("PresetLibraryEmpty"));
        _empty.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(_empty, "Preset library empty");

        var listGrid = new Grid();
        listGrid.Children.Add(_list);
        listGrid.Children.Add(_empty);

        var root = new DockPanel();
        DockPanel.SetDock(_search, Dock.Top);
        root.Children.Add(_search);
        root.Children.Add(listGrid);
        Content = root;

        Refresh();
    }

    /// <summary>Lọc danh sách preset theo từ khoá (không phân biệt dấu).</summary>
    private void ApplyFilter()
    {
        var selected = (_list.SelectedItem as Preset)?.Name;
        var query = _search.Text;
        var shown = _all.Where(p => BFR.Presentation.Design.MacNavigation.Matches(query, p.DisplayName)).ToList();
        _list.ItemsSource = shown;
        _list.SelectedItem = shown.FirstOrDefault(p => p.Name == selected);

        _empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSelection();
    }

    private void Refresh()
    {
        _all = _store.LoadAll().ToList();
        ApplyFilter();
        SetStatus(CountText());
    }

    private string CountText()
    {
        // Dùng _all.Count đã cache trong bộ nhớ, không đọc đĩa lại mỗi lần.
        var count = _all.Count;
        if (count == 0) return Loc.T("LibraryEmpty");

        var max = _license.MaxSavedPresets;
        return max == int.MaxValue
            ? $"{count} preset"
            : $"{count}/{max} preset — " + Loc.T("LibraryLimitHint");
    }

    private void SetStatus(string text)
    {
        _status = text;
        ShellContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowSelection()
    {
        var preset = _list.SelectedItem as Preset;
        _applyButton.IsEnabled = preset is not null;
        _shareButton.IsEnabled = preset is not null;
        _deleteButton.IsEnabled = preset is not null;

        if (preset is null)
        {
            _detail.ShowEmpty(Loc.T("MacPresetSelectHint"), keepFooter: true);
            return;
        }

        _detail.Show(preset.DisplayName, preset.DisplayDescription);
        _detail.AddSection(Loc.T("MacTabRules"));
        if (preset.Rules.Count == 0) _detail.AddRow(string.Empty, "—");

        var number = 0;
        foreach (var rule in preset.Rules)
        {
            var summary = RuleSummaries.Build(rule, _strings);
            if (!rule.Enabled) summary = $"{summary} ({Loc.T("ValueOff")})".Trim();
            _detail.AddRow($"{++number}. {RuleTitles.Get(rule.Type, rule.Name, _strings)}", summary);
        }
    }

    private void SaveCurrent(Preset current)
    {
        var existing = _store.LoadAll();
        var alreadyExists = existing.Any(p => p.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase));
        if (!alreadyExists)
        {
            var gate = _license.CheckPresetCount(existing.Count + 1);
            if (!gate.Allowed)
            {
                MacAlert.Show(gate.Message, Loc.T("ProLockedTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        _store.Save(current);
        Refresh();
        SetStatus(Loc.F("LibrarySaved", current.Name));
    }

    private void ApplySelected()
    {
        if (_list.SelectedItem is Preset preset)
        {
            PresetApplied?.Invoke(this, preset);
        }
    }

    private void DeleteSelected()
    {
        if (_list.SelectedItem is not Preset preset) return;

        var confirmTemplate = Application.Current?.TryFindResource("PresetDeleteConfirm") as string
            ?? "Delete preset \"{0}\"?";
        var owner = Window.GetWindow(this);
        var answer = owner is not null
            ? MacAlert.Show(owner, string.Format(confirmTemplate, preset.DisplayName), Loc.T("LibraryDelete"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MacAlert.Show(string.Format(confirmTemplate, preset.DisplayName), Loc.T("LibraryDelete"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _store.Delete(preset.Name);
        Refresh();
    }

    /// <summary>Chia sẻ preset: chép JSON vào bảng nhớ tạm để gửi cho người khác.</summary>
    private void ShareSelected()
    {
        if (_list.SelectedItem is not Preset preset) return;

        try
        {
            Clipboard.SetText(_store.ToJson(preset));
            SetStatus(Loc.T("PresetCopied"));
        }
        catch (Exception ex)
        {
            MacAlert.Show(ex.Message, Loc.T("LibraryTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Nhập preset từ bảng nhớ tạm (preset được chia sẻ).</summary>
    private void ImportFromClipboard()
    {
        try
        {
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text)) return;

            var preset = _store.FromJson(text);
            var existing = _store.LoadAll();
            var gate = _license.CheckPresetCount(existing.Count + 1);
            if (!gate.Allowed)
            {
                MacAlert.Show(gate.Message, Loc.T("ProLockedTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _store.Save(preset);
            Refresh();
            SetStatus(Loc.F("PresetImported", preset.DisplayName));
        }
        catch (Exception ex)
        {
            MacAlert.Show(ex.Message, Loc.T("LibraryTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
