namespace BFR.App;

using BFR.App.Controls.Mac;
using BFR.Core;
using BFR.Core.Licensing;
using BFR.Core.WatchFolder;
using BFR.Presentation.Tools;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using BFR.App.Infrastructure;
using System.Windows.Controls;
using System.Windows.Automation;

/// <summary>
/// Watch Folder nhiều thư mục (B6, Pro): mỗi quy tắc theo dõi một thư mục và chạy một quy trình
/// (đổi tên theo preset, hoặc cả workflow nhiều bước) cho tệp mới. Các quy tắc được lưu và chạy nền cùng lúc.
/// </summary>
public class WatchFolderPage : PageBase, IMacShellContent
{
    private sealed record PresetChoice(Preset Preset)
    {
        public override string ToString() => Preset.DisplayName;
    }

    private sealed record KindChoice(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly PresetStore _presetStore;
    private readonly LicenseService _license;
    private readonly RealFileSystem _fs = new();
    private readonly string _journalDir;
    private readonly string _logPath;
    private readonly WatchRuleStore _store;
    private readonly List<WatchRule> _rules;

    private readonly ListBox _ruleList = new();
    private readonly TextBox _nameBox = new();
    private readonly TextBox _folderBox = new();
    private readonly ComboBox _kindCombo = new();
    private readonly ComboBox _presetCombo = new();
    private readonly TextBox _workflowFileBox = new();
    private readonly CheckBox _enabled = new() { IsChecked = true };
    private readonly CheckBox _recursive = new() { IsChecked = true };
    private readonly CheckBox _dryRun = new();
    private readonly TextBox _settleBox = new() { Text = "1500", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _includeBox = new();
    private readonly TextBox _excludeBox = new();
    private readonly ListBox _logList = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(14, 0, 14, 8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _logEmpty = new();
    private StackPanel _ruleEmpty = null!;
    private readonly MacDetailPane _detail = new();
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal };
    private Button _startButton = null!;
    private Button _stopButton = null!;
    private Button _addButton = null!;
    private Button _removeButton = null!;
    private readonly List<FrameworkElement> _presetRows = new();
    private readonly List<FrameworkElement> _fileRows = new();

    public event EventHandler? ShellContentChanged;

    FrameworkElement? IMacShellContent.ToolbarCenter => null;

    FrameworkElement? IMacShellContent.ToolbarTrailing => _toolbar;

    FrameworkElement? IMacShellContent.Inspector => _rules.Count == 0 ? null : _detail;

    string? IMacShellContent.StatusText => null;

    private WatchFolderManager? _manager;
    private bool _loading;
    private WatchRule? Selected => _ruleList.SelectedItem as WatchRule;

    /// <summary>True khi dịch vụ Watch Folder đang chạy nền.</summary>
    public bool IsRunning => _manager is not null;

    /// <summary>Số quy tắc đang bật.</summary>
    public int EnabledRuleCount => _rules.Count(r => r.Enabled);

    /// <summary>Bật/tắt nhanh dịch vụ theo dõi (dùng từ thẻ HomePage).</summary>
    public void ToggleRunning()
    {
        if (_manager is not null) Stop();
        else Start();
    }

    public WatchFolderPage(PresetStore presetStore, LicenseService license)
    {
        _presetStore = presetStore;
        _license = license;

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatchFileRenamer");
        _journalDir = Path.Combine(appData, "journal");
        _logPath = Path.Combine(appData, "watch", "watch.log");
        _store = new WatchRuleStore(_fs, Path.Combine(appData, "watch", "rules.json"));
        _rules = _store.Load();

        AutomationProperties.SetName(this, "Watch folder window");

        BuildToolbar();
        ApplyStyles();
        BuildEditor();
        Content = BuildContent();

        FillKinds();
        RefreshPresets();
        WireEditorEvents();

        if (_rules.Count > 0) _ruleList.SelectedIndex = 0;
        UpdateEditorState();
        _stopButton.IsEnabled = false;
    }

    private void BuildToolbar()
    {
        _addButton = MacDetailPane.CreateIconButton("Plus", Loc.T("WatchAddRule"));
        _removeButton = MacDetailPane.CreateIconButton("Trash2", Loc.T("WatchRemoveRule"));
        _removeButton.Margin = new Thickness(0, 0, 10, 0);
        _startButton = MacDetailPane.CreateButton(Loc.T("WatchStart"), "Mac.Button.Primary");
        _stopButton = MacDetailPane.CreateButton(Loc.T("WatchStop"));
        _stopButton.Margin = new Thickness(6, 0, 0, 0);
        _toolbar.Children.Add(_addButton);
        _toolbar.Children.Add(_removeButton);
        _toolbar.Children.Add(_startButton);
        _toolbar.Children.Add(_stopButton);

        _addButton.Click += (_, _) => AddRule();
        _removeButton.Click += (_, _) => RemoveRule();
        _startButton.Click += (_, _) => Start();
        _stopButton.Click += (_, _) => Stop();
    }

    /// <summary>Form chỉnh quy tắc trong inspector: nhãn nằm trên ô nhập vì bảng chỉ rộng 300 px.</summary>
    private void BuildEditor()
    {
        _detail.Show(Loc.T("WatchOptionsTitle"));
        var editor = _detail.Body;
        editor.Children.Add(Row(Loc.T("WatchRuleName"), _nameBox));
        editor.Children.Add(Row(Loc.Plain("WatchFolder"), _folderBox, BrowseButton()));
        editor.Children.Add(Row(Loc.T("WatchKind"), _kindCombo));
        var presetRow = Row(Loc.T("WatchPreset"), _presetCombo);
        var fileRow = Row(Loc.T("WatchWorkflowFile"), _workflowFileBox, WorkflowBrowseButton());
        _presetRows.Add(presetRow);
        _fileRows.Add(fileRow);
        editor.Children.Add(presetRow);
        editor.Children.Add(fileRow);
        editor.Children.Add(Row(Loc.T("WatchSettle"), _settleBox));
        editor.Children.Add(Row(Loc.T("WatchInclude"), _includeBox));
        editor.Children.Add(Row(Loc.T("WatchExclude"), _excludeBox));
        foreach (var check in new[] { _enabled, _recursive, _dryRun })
        {
            check.Margin = new Thickness(0, 8, 0, 0);
            editor.Children.Add(check);
        }
    }

    private UIElement BuildContent()
    {
        // Danh sách quy tắc: mỗi hàng có tên, thư mục và công tắc bật/tắt.
        _ruleList.ItemsSource = _rules;
        _ruleList.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_ruleList, ScrollBarVisibility.Disabled);
        _ruleList.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <DockPanel Margin="0,4">
                <CheckBox DockPanel.Dock="Right" IsChecked="{Binding Enabled, Mode=TwoWay}" Style="{DynamicResource Mac.Switch}"
                          VerticalAlignment="Center" Margin="12,0,0,0" ToolTip="{Binding Name}" />
                <StackPanel>
                  <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis"
                             Foreground="{Binding RelativeSource={RelativeSource AncestorType=ListBoxItem}, Path=Foreground}" />
                  <TextBlock Text="{Binding Folder}" FontSize="11.5" Opacity="0.75" TextTrimming="CharacterEllipsis"
                             Foreground="{Binding RelativeSource={RelativeSource AncestorType=ListBoxItem}, Path=Foreground}" />
                </StackPanel>
              </DockPanel>
            </DataTemplate>
            """);
        _ruleList.SelectionChanged += (_, _) => LoadSelected();
        _ruleList.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(CardToggle_Changed));
        _ruleList.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler(CardToggle_Changed));
        AutomationProperties.SetName(_ruleList, "Watch rules");

        // Chưa có quy tắc nào: trạng thái rỗng có nút thêm; inspector đóng lại.
        _ruleEmpty = MacDetailPane.CreateEmptyState("Eye", Loc.T("WatchRulesEmptyTitle"), Loc.T("WatchRulesEmptyHint"));
        var addFirst = MacDetailPane.CreateButton(Loc.T("WatchAddRule"), "Mac.Button.Primary", "Add first watch rule");
        addFirst.HorizontalAlignment = HorizontalAlignment.Center;
        addFirst.Margin = new Thickness(0, 14, 0, 0);
        addFirst.Click += (_, _) => AddRule();
        _ruleEmpty.Children.Add(addFirst);

        var rules = new Grid();
        rules.Children.Add(_ruleList);
        rules.Children.Add(_ruleEmpty);

        // Nhật ký ở đáy.
        _logList.ItemsSource = new List<string>();
        AutomationProperties.SetName(_logList, "Watch folder log");
        _logEmpty.Text = Loc.T("WatchFolderEmpty");
        _logEmpty.TextWrapping = TextWrapping.Wrap;
        _logEmpty.TextAlignment = TextAlignment.Center;
        _logEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _logEmpty.VerticalAlignment = VerticalAlignment.Center;
        _logEmpty.Margin = new Thickness(24);
        _logEmpty.SetResourceReference(StyleProperty, "Mac.Callout");

        var logTitle = new TextBlock { Text = Loc.T("WatchLogTitle"), Margin = new Thickness(16, 8, 16, 2) };
        logTitle.SetResourceReference(StyleProperty, "Mac.Section");

        var logGrid = new Grid();
        logGrid.Children.Add(_logList);
        logGrid.Children.Add(_logEmpty);

        var log = new DockPanel();
        DockPanel.SetDock(logTitle, Dock.Top);
        log.Children.Add(logTitle);
        DockPanel.SetDock(_status, Dock.Bottom);
        log.Children.Add(_status);
        log.Children.Add(logGrid);
        var logBorder = new Border { Height = 190, BorderThickness = new Thickness(0, 1, 0, 0), Child = log };
        logBorder.SetResourceReference(Border.BorderBrushProperty, "Mac.Separator");

        var root = new DockPanel();
        DockPanel.SetDock(logBorder, Dock.Bottom);
        root.Children.Add(logBorder);
        root.Children.Add(rules);
        return root;
    }

    private void CardToggle_Changed(object sender, RoutedEventArgs e)
    {
        // Công tắc cũng phát Checked khi đang dựng giao diện (giá trị đến từ binding): không phải người dùng bấm.
        if (_loading || e.OriginalSource is not FrameworkElement { IsLoaded: true }) return;
        Save();
        // Sự kiện nổi lên từ công tắc: sender là cả danh sách, quy tắc nằm ở DataContext của OriginalSource.
        if (e.OriginalSource is FrameworkElement { DataContext: WatchRule rule } && ReferenceEquals(rule, Selected))
        {
            _loading = true;
            _enabled.IsChecked = rule.Enabled;
            _loading = false;
        }
    }

    private void ApplyStyles()
    {
        foreach (var box in new[] { _nameBox, _folderBox, _workflowFileBox, _settleBox, _includeBox, _excludeBox })
            box.SetResourceReference(StyleProperty, "Mac.TextBox");
        foreach (var combo in new[] { _kindCombo, _presetCombo })
            combo.SetResourceReference(StyleProperty, "Mac.ComboBox");
        _ruleList.SetResourceReference(StyleProperty, "Mac.ListBox");
        _logList.SetResourceReference(StyleProperty, "Mac.ListBox");
        foreach (var check in new[] { _enabled, _recursive, _dryRun })
            check.SetResourceReference(StyleProperty, "Mac.CheckBox");
        _enabled.Content = Loc.T("WatchRuleEnabled");
        _recursive.Content = Loc.T("WatchRecursive");
        _dryRun.Content = Loc.T("WatchDryRun");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label2");
    }

    private static StackPanel Row(string label, FrameworkElement element, UIElement? extra = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var text = new TextBlock { Text = label, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label2");
        panel.Children.Add(text);
        AutomationProperties.SetName(element, label.TrimEnd(':', '：', ' '));

        if (extra is null)
        {
            panel.Children.Add(element);
            return panel;
        }

        var line = new DockPanel();
        DockPanel.SetDock(extra, Dock.Right);
        line.Children.Add(extra);
        line.Children.Add(element);
        panel.Children.Add(line);
        return panel;
    }

    private Button BrowseButton()
    {
        var button = MacDetailPane.CreateButton("…", automationName: Loc.T("WatchBrowseButton"));
        button.Margin = new Thickness(6, 0, 0, 0);
        button.ToolTip = Loc.T("WatchBrowseButton");
        button.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = Loc.Plain("WatchFolder") };
            if (dialog.ShowDialog() == true) _folderBox.Text = dialog.FolderName;
        };
        return button;
    }

    private Button WorkflowBrowseButton()
    {
        var button = MacDetailPane.CreateButton("…", automationName: Loc.T("WatchWorkflowFile"));
        button.Margin = new Thickness(6, 0, 0, 0);
        button.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Workflow|*.json" };
            if (dialog.ShowDialog() == true) _workflowFileBox.Text = dialog.FileName;
        };
        return button;
    }

    // ----------------------------------------------------------------- Quy tắc

    private void FillKinds()
    {
        _kindCombo.ItemsSource = new List<KindChoice>
        {
            new("preset", Loc.T("WatchKindPreset")),
            new("photos", Loc.T("ToolWorkflowPhotos")),
            new("tidy", Loc.T("ToolWorkflowTidy")),
            new("file", Loc.T("ToolWorkflowFromFile"))
        };
        _kindCombo.SelectedIndex = 0;
    }

    private void RefreshPresets()
    {
        var presets = _presetStore.LoadAll().Concat(BuiltInPresets.All).Select(p => new PresetChoice(p)).ToList();
        _presetCombo.ItemsSource = presets;
        if (presets.Count > 0) _presetCombo.SelectedIndex = 0;
    }

    private void WireEditorEvents()
    {
        _nameBox.TextChanged += (_, _) => Commit();
        _folderBox.TextChanged += (_, _) => Commit();
        _workflowFileBox.TextChanged += (_, _) => Commit();
        _settleBox.TextChanged += (_, _) => Commit();
        _includeBox.TextChanged += (_, _) => Commit();
        _excludeBox.TextChanged += (_, _) => Commit();
        _kindCombo.SelectionChanged += (_, _) => { UpdateEditorState(); Commit(); };
        _presetCombo.SelectionChanged += (_, _) => Commit();
        foreach (var c in new[] { _enabled, _recursive, _dryRun }) c.Click += (_, _) => Commit();
    }

    private void AddRule()
    {
        if (_rules.Count >= WatchRule.MaxRules)
        {
            _status.Text = Loc.F("CoreWatchTooMany", WatchRule.MaxRules);
            return;
        }

        var rule = new WatchRule { Name = Loc.F("WatchRuleDefaultName", _rules.Count + 1), Folder = string.Empty };
        rule.Steps.Add(new RenameStep(BuiltInPresets.All.First()));
        _rules.Add(rule);
        RefreshList();
        _ruleList.SelectedItem = rule;
    }

    private void RemoveRule()
    {
        if (Selected is not { } rule) return;
        _rules.Remove(rule);
        RefreshList();
        if (_rules.Count > 0) _ruleList.SelectedIndex = 0;
        Save();
    }

    private void RefreshList()
    {
        var selected = _ruleList.SelectedItem;
        _ruleList.ItemsSource = null;
        _ruleList.ItemsSource = _rules;
        if (selected is not null && _rules.Contains(selected)) _ruleList.SelectedItem = selected;
        UpdateEditorState();
    }

    private void LoadSelected()
    {
        _loading = true;
        try
        {
            if (Selected is not { } rule)
            {
                UpdateEditorState();
                return;
            }

            _nameBox.Text = rule.Name;
            _folderBox.Text = rule.Folder;
            _enabled.IsChecked = rule.Enabled;
            _recursive.IsChecked = rule.Recursive;
            _dryRun.IsChecked = rule.DryRun;
            _settleBox.Text = rule.SettleDelayMs.ToString();
            _includeBox.Text = rule.IncludeGlob ?? string.Empty;
            _excludeBox.Text = rule.ExcludeGlob ?? string.Empty;
            _workflowFileBox.Text = rule.WorkflowFilePath ?? string.Empty;

            // Suy ra kiểu quy trình từ tệp workflow đã lưu hoặc danh sách bước.
            if (!string.IsNullOrWhiteSpace(rule.WorkflowFilePath))
            {
                _kindCombo.SelectedIndex = 3;
            }
            else if (rule.Steps is [RenameStep rename])
            {
                _kindCombo.SelectedIndex = 0;
                var presets = _presetCombo.ItemsSource as List<PresetChoice>;
                var match = presets?.FirstOrDefault(p => p.Preset.Name == rename.Preset.Name);
                if (match is not null) _presetCombo.SelectedItem = match;
            }
            else
            {
                var template = WorkflowTool.Templates().FirstOrDefault(t => t.Definition.Steps.SequenceEqual(rule.Steps)).Value;
                _kindCombo.SelectedIndex = template switch { "photos" => 1, "tidy" => 2, _ => 3 };
            }
        }
        finally
        {
            _loading = false;
        }
        UpdateEditorState();
    }

    private void UpdateEditorState()
    {
        var has = Selected is not null;
        var noRules = _rules.Count == 0;
        var wasEmpty = _ruleEmpty.Visibility == Visibility.Visible;
        _ruleEmpty.Visibility = noRules ? Visibility.Visible : Visibility.Collapsed;
        _ruleList.Visibility = noRules ? Visibility.Collapsed : Visibility.Visible;
        if (wasEmpty != noRules) ShellContentChanged?.Invoke(this, EventArgs.Empty);
        foreach (var c in new UIElement[] { _nameBox, _folderBox, _kindCombo, _presetCombo, _workflowFileBox, _enabled, _recursive, _dryRun, _settleBox, _includeBox, _excludeBox })
            c.IsEnabled = has;
        _removeButton.IsEnabled = has && _manager is null;

        var kind = (_kindCombo.SelectedItem as KindChoice)?.Value ?? "preset";
        foreach (var row in _presetRows) row.Visibility = kind == "preset" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in _fileRows) row.Visibility = kind == "file" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Ghi giá trị trên màn hình vào quy tắc đang chọn.</summary>
    private void Commit()
    {
        if (_loading || Selected is not { } rule) return;

        rule.Name = _nameBox.Text.Trim();
        rule.Folder = _folderBox.Text.Trim();
        rule.Enabled = _enabled.IsChecked == true;
        rule.Recursive = _recursive.IsChecked == true;
        rule.DryRun = _dryRun.IsChecked == true;
        rule.SettleDelayMs = int.TryParse(_settleBox.Text, out var settle) ? settle : 1500;
        rule.IncludeGlob = string.IsNullOrWhiteSpace(_includeBox.Text) ? null : _includeBox.Text.Trim();
        rule.ExcludeGlob = string.IsNullOrWhiteSpace(_excludeBox.Text) ? null : _excludeBox.Text.Trim();

        var kind = (_kindCombo.SelectedItem as KindChoice)?.Value ?? "preset";
        switch (kind)
        {
            case "photos":
            case "tidy":
                rule.WorkflowFilePath = null;
                rule.Steps = WorkflowTool.Templates().First(t => t.Value == kind).Definition.Steps.ToList();
                break;
            case "file":
                rule.WorkflowFilePath = string.IsNullOrWhiteSpace(_workflowFileBox.Text) ? null : _workflowFileBox.Text.Trim();
                break;
            default:
                rule.WorkflowFilePath = null;
                if (_presetCombo.SelectedItem is PresetChoice choice) rule.Steps = new List<WorkflowStep> { new RenameStep(choice.Preset) };
                break;
        }

        UpdateEditorState();
        var index = _ruleList.SelectedIndex;
        // Cập nhật tiêu đề trong danh sách mà không mất lựa chọn.
        _ruleList.Items.Refresh();
        _ruleList.SelectedIndex = index;
    }

    private void Save()
    {
        try { _store.Save(_rules); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
        }
    }

    // ----------------------------------------------------------------- Chạy

    private List<WatchRule>? BuildRunnableRules()
    {
        var rules = new List<WatchRule>();
        foreach (var rule in _rules)
        {
            var copy = new WatchRule
            {
                Id = rule.Id, Name = rule.Name, Enabled = rule.Enabled, Folder = rule.Folder, Recursive = rule.Recursive,
                IncludeGlob = rule.IncludeGlob, ExcludeGlob = rule.ExcludeGlob, SettleDelayMs = rule.SettleDelayMs,
                MaxFilesPerBatch = rule.MaxFilesPerBatch, DryRun = rule.DryRun, WorkflowFilePath = rule.WorkflowFilePath,
                Steps = rule.Steps.ToList()
            };

            // Quy tắc dùng tệp workflow: nạp theo đường dẫn lưu riêng trên từng rule để chạy song song nhiều rule.
            if (rule.Enabled && !string.IsNullOrWhiteSpace(rule.WorkflowFilePath))
            {
                try { copy.Steps = WorkflowSerializer.Parse(File.ReadAllText(rule.WorkflowFilePath)).Steps; }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or ArgumentException or IOException)
                {
                    _status.Text = Loc.F("CoreToolWorkflowBad", ex.Message);
                    return null;
                }
            }
            rules.Add(copy);
        }
        return rules;
    }

    private void Start()
    {
        if (!_license.Allows(ProFeature.UnlimitedRecursion))
        {
            MacAlert.Show(FeatureMessages.For(ProFeature.UnlimitedRecursion), Loc.T("ProLockedTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_license.Allows(ProFeature.Workflow))
        {
            MacAlert.Show(FeatureMessages.For(ProFeature.Workflow), Loc.T("ProLockedTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_rules.Count(r => r.Enabled) == 0)
        {
            MacAlert.Show(Loc.T("WatchFolderMissing"), Loc.T("WatchTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var runnable = BuildRunnableRules();
        if (runnable is null) return;

        var errors = WatchFolderManager.Validate(runnable);
        if (errors.Count > 0)
        {
            _status.Text = string.Join(Environment.NewLine, errors.Take(3));
            return;
        }

        foreach (var rule in runnable.Where(r => r.Enabled))
        {
            if (!Directory.Exists(rule.Folder))
            {
                _status.Text = Loc.T("WatchFolderMissing") + " " + rule.Folder;
                return;
            }
        }

        Save();

        try
        {
            Stop();

            var metadata = new CompositeMetadataProvider(new ExifMetadataReader(), new MediaMetadataReader(), new AudioTagReader());
            _manager = new WatchFolderManager(_fs, new WorkflowWatchProcessor(_fs, _journalDir, metadata, documents: new BFR.Documents.Workflow.DocumentWorkflowStepExecutor(_license.Allows)));
            _manager.Logged += (_, e) => AppendLog(e.Rule, e.Entry);
            _manager.BatchProcessed += (_, e) => Dispatcher.Invoke(() =>
                _status.Text = $"{DisplayName(e.Rule)}: " + Loc.F("WatchBatchDone", e.Result.Renamed, e.Result.Unchanged, e.Result.Skipped, e.Result.Failed));
            _manager.Start(runnable);

            _startButton.IsEnabled = false;
            _stopButton.IsEnabled = true;
            UpdateEditorState();
            _status.Text = Loc.F("WatchRunning", _manager.RunningCount);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _manager?.Dispose();
            _manager = null;
            MacAlert.Show(ex.Message, Loc.T("WatchTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Stop()
    {
        if (_manager is null) return;

        _manager.Dispose();
        _manager = null;
        _startButton.IsEnabled = true;
        _stopButton.IsEnabled = false;
        UpdateEditorState();
    }

    private static string DisplayName(WatchRule rule) => string.IsNullOrWhiteSpace(rule.Name) ? rule.Folder : rule.Name;

    private void AppendLog(WatchRule rule, WatchFolderLogEntry entry)
    {
        var line = $"[{DisplayName(rule)}] {entry.Format()}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, LogRedactor.Redact(line) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nhật ký là thông tin phụ: không làm hỏng chức năng.
        }

        Dispatcher.Invoke(() =>
        {
            if (_logList.ItemsSource is List<string> items)
            {
                items.Add(line);
                while (items.Count > 500) items.RemoveAt(0);
                _logList.Items.Refresh();
                _logEmpty.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            }
        });
    }

    /// <summary>Dịch vụ theo dõi chạy nền ngay cả khi người dùng chuyển sang trang khác; chỉ dừng khi cửa sổ chính đóng.</summary>
    public override void Shutdown() => Stop();
}
