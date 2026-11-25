namespace BFR.App;

using BFR.App.Controls.Mac;
using BFR.Core;
using BFR.Core.Licensing;
using BFR.Presentation.Design;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

/// <summary>
/// Lịch sử và Hoàn tác (master–detail): các lần xử lý nhóm theo ngày ở vùng nội dung; chi tiết, xem trước
/// "tên hiện tại → tên sẽ trả về" và nút Hoàn tác ở inspector.
/// </summary>
public sealed class HistoryPage : PageBase, IMacShellContent
{
    private const int MaxPreviewRows = 500;

    private sealed class HistoryRow
    {
        public HistoryRow(HistoryEntry entry, DateTime nowLocal)
        {
            Entry = entry;
            var utc = entry.StartedUtc.Kind == DateTimeKind.Utc ? entry.StartedUtc : DateTime.SpecifyKind(entry.StartedUtc, DateTimeKind.Utc);
            Group = HistoryDays.Of(utc.ToLocalTime(), nowLocal);
            GroupTitle = Loc.T(HistoryDays.TitleKey(Group));
            TimeDisplay = FormatLocalAndRelative(entry.StartedUtc);
            Title = string.IsNullOrEmpty(entry.PresetText) ? Loc.T("MacHistoryRunDefault") : entry.PresetText;
            Detail = $"{Loc.F("MacFilesCount", entry.Total)} · {TimeDisplay}";
        }

        public HistoryEntry Entry { get; }
        public HistoryDayGroup Group { get; }
        public string GroupTitle { get; }
        public string TimeDisplay { get; }
        public string Title { get; }
        public string Detail { get; }
        public string StatusText => Entry.StatusText;
        public bool HasFailures => Entry.Failed > 0;
        public override string ToString() => $"{Title}, {Detail}, {StatusText}";
    }

    private sealed record UndoPreviewPair(string CurrentName, string OriginalName, string StateText, bool IsMissing)
    {
        public string Target => "→ " + OriginalName;
        public override string ToString() => $"{CurrentName} → {OriginalName}, {StateText}";
    }

    private readonly HistoryStore _history;
    private readonly IFileSystem _fs;
    private readonly LicenseService _license;
    private readonly ListBox _list = new();
    private readonly StackPanel _empty;
    private readonly MacDetailPane _detail = new();
    private readonly ListBox _pairs = new();
    private readonly TextBlock _previewSummary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
    private readonly Button _undo;
    private readonly Button _refresh;
    private CancellationTokenSource? _previewCts;
    private int _lastPreviewTotal;
    private int _lastPreviewMissing;
    private string _status = string.Empty;

    /// <summary>Trang bị rời đi: cửa sổ chính tính lại xem trước (tên tệp có thể đã được hoàn tác).</summary>
    public event EventHandler? Left;

    public event EventHandler? ShellContentChanged;

    FrameworkElement? IMacShellContent.ToolbarCenter => null;

    FrameworkElement? IMacShellContent.ToolbarTrailing => _refresh;

    FrameworkElement? IMacShellContent.Inspector => _detail;

    string? IMacShellContent.StatusText => _status;

    public override void OnShown() => Refresh();

    public override void OnHidden()
    {
        _previewCts?.Cancel();
        Left?.Invoke(this, EventArgs.Empty);
    }

    public HistoryPage(HistoryStore history, IFileSystem fs, LicenseService? license = null)
    {
        _history = history;
        _fs = fs;
        _license = license ?? new LicenseService(new LicenseStore(new RealFileSystem()), trialEnabled: false);

        AutomationProperties.SetName(this, "History and Undo Window");

        _list.SetResourceReference(StyleProperty, "Mac.ListBox");
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _list.SelectionMode = SelectionMode.Single;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizingWhenGrouping(_list, true);
        _list.ItemTemplate = BuildRowTemplate();
        _list.GroupStyle.Add(new GroupStyle { HeaderTemplate = BuildGroupHeaderTemplate() });
        _list.SelectionChanged += (_, _) => _ = ShowSelectionAsync();
        AutomationProperties.SetName(_list, "Rename Runs History Table");

        _empty = MacDetailPane.CreateEmptyState("History", Loc.T("HistoryEmptyTitle"), Loc.T("HistoryEmpty"));
        AutomationProperties.SetName(_empty, "History Empty");

        var root = new Grid();
        root.Children.Add(_list);
        root.Children.Add(_empty);
        Content = root;

        _refresh = MacDetailPane.CreateButton(Loc.T("RefreshHistory"), automationName: "Refresh History List");
        _refresh.Content = Loc.Plain("RefreshHistory");
        _refresh.Click += (_, _) => Refresh();

        _undo = MacDetailPane.CreateButton(Loc.Plain("UndoSelected"), "Mac.Button.Primary", "Undo Selected Run");
        _undo.Click += Undo_Click;
        _detail.Footer.Children.Add(_undo);

        _previewSummary.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label3");
        _pairs.SetResourceReference(StyleProperty, "Mac.ListBox");
        _pairs.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_pairs, ScrollBarVisibility.Disabled);
        _pairs.ItemTemplate = BuildPairTemplate();
        AutomationProperties.SetName(_pairs, "Undo Preview Pairs");
        _detail.SetFill(_pairs);

        Refresh();
    }

    /// <summary>Chọn một lần xử lý theo mã (bấm từ mục Gần đây ở trang Bắt đầu).</summary>
    public void SelectRun(string id)
    {
        var row = _list.Items.OfType<HistoryRow>().FirstOrDefault(r => r.Entry.Id == id);
        if (row is null) return;
        _list.SelectedItem = row;
        _list.ScrollIntoView(row);
    }

    private static string TryLoc(string key, string fallback)
    {
        var value = Application.Current?.TryFindResource(key) as string;
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    internal static string FormatLocalAndRelative(DateTime utc)
    {
        var utcTime = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = utcTime.ToLocalTime();
        var elapsed = DateTime.UtcNow - utcTime;
        var relative = FormatRelative(elapsed);
        var clock = local.Date == DateTime.Today
            ? local.ToString("HH:mm")
            : local.ToString("yyyy-MM-dd HH:mm");
        return $"{relative} · {clock}";
    }

    internal static string FormatRelative(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 60) return TryLoc("TimeJustNow", "just now");
        if (elapsed.TotalMinutes < 60) return string.Format(TryLoc("TimeMinutesAgo", "{0}m ago"), (int)elapsed.TotalMinutes);
        if (elapsed.TotalHours < 24) return string.Format(TryLoc("TimeHoursAgo", "{0}h ago"), (int)elapsed.TotalHours);
        return string.Format(TryLoc("TimeDaysAgo", "{0}d ago"), (int)elapsed.TotalDays);
    }

    private void Refresh()
    {
        try { _history.RegisterInterrupted(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }

        var selected = (_list.SelectedItem as HistoryRow)?.Entry.Id;
        var now = DateTime.Now;
        var rows = _history.LoadAll()
            .Take(_license.MaxUndoEntries == int.MaxValue ? int.MaxValue : _license.MaxUndoEntries)
            .Select(e => new HistoryRow(e, now))
            .OrderBy(r => r.Group)
            .ThenByDescending(r => r.Entry.StartedUtc)
            .ToList();

        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HistoryRow.GroupTitle)));
        _list.ItemsSource = view;
        _list.SelectedItem = rows.FirstOrDefault(r => r.Entry.Id == selected);
        _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _status = Loc.F("MacHistoryCount", rows.Count);
        ShellContentChanged?.Invoke(this, EventArgs.Empty);
        if (_list.SelectedItem is null) _ = ShowSelectionAsync();
    }

    private async Task ShowSelectionAsync()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        _lastPreviewTotal = 0;
        _lastPreviewMissing = 0;
        _pairs.ItemsSource = null;

        if (_list.SelectedItem is not HistoryRow row)
        {
            _undo.IsEnabled = false;
            _detail.ShowEmpty(Loc.T("MacHistorySelectHint"), keepFooter: true);
            return;
        }

        var entry = row.Entry;
        _undo.IsEnabled = !string.IsNullOrWhiteSpace(entry.JournalPath);
        _detail.Show(row.Title, row.TimeDisplay);
        _detail.AddRow(Loc.T("HistoryColStatus"), row.StatusText);
        _detail.AddRow(Loc.T("HistoryColTotal"), entry.Total.ToString());
        _detail.AddRow(Loc.T("HistoryColOk"), entry.Succeeded.ToString());
        if (entry.Failed > 0) _detail.AddRow(Loc.T("HistoryColFailed"), entry.Failed.ToString());
        _detail.AddRow(Loc.T("HistoryColId"), entry.Id);
        _detail.AddSection(TryLoc("HistoryPreviewTitle", "Undo preview"));
        _detail.Body.Children.Add(_previewSummary);

        if (string.IsNullOrWhiteSpace(entry.JournalPath))
        {
            _previewSummary.Text = TryLoc("HistoryPreviewUnavailable", "Journal details unavailable.");
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        var token = cts.Token;
        _previewSummary.Text = TryLoc("HistoryPreviewLoading", "Loading journal…");

        try
        {
            var journalPath = entry.JournalPath;
            var (pairs, total, missing) = await Task.Run(() => LoadPreviewPairs(journalPath, token), token);
            if (token.IsCancellationRequested) return;

            _lastPreviewTotal = total;
            _lastPreviewMissing = missing;
            _pairs.ItemsSource = pairs;

            var shownNote = total > MaxPreviewRows
                ? string.Format(TryLoc("HistoryPreviewCapped", "Showing first {0} of {1} files"), MaxPreviewRows, total)
                : string.Format(TryLoc("HistoryPreviewCount", "{0} files"), total);
            if (missing > 0)
            {
                shownNote += " · " + string.Format(TryLoc("HistoryPreviewMissingCount", "{0} missing/moved"), missing);
            }
            _previewSummary.Text = shownNote;
        }
        catch (OperationCanceledException)
        {
            // Đã chuyển sang dòng khác.
        }
        catch
        {
            if (!token.IsCancellationRequested)
                _previewSummary.Text = TryLoc("HistoryPreviewUnavailable", "Journal details unavailable.");
        }
    }

    private (List<UndoPreviewPair> Pairs, int Total, int Missing) LoadPreviewPairs(string journalPath, CancellationToken token)
    {
        if (!_fs.FileExists(journalPath)) return (new List<UndoPreviewPair>(), 0, 0);

        var steps = UndoService.ReadSteps(journalPath, _fs)
            .Where(s => s.Type == "step" && s.Status == "done")
            .GroupBy(s => s.Seq)
            .Select(g => g.Last())
            .OrderByDescending(s => s.Seq)
            .ToList();

        var pairs = new List<UndoPreviewPair>(Math.Min(steps.Count, MaxPreviewRows));
        var missing = 0;
        var okLabel = TryLoc("HistoryPreviewReady", "Ready");
        var missingLabel = TryLoc("HistoryPreviewMissing", "Missing/moved");

        foreach (var step in steps)
        {
            token.ThrowIfCancellationRequested();
            var currentPath = step.To ?? string.Empty;
            var originalPath = step.From ?? string.Empty;
            var exists = !string.IsNullOrEmpty(currentPath) && (_fs.FileExists(currentPath) || _fs.DirectoryExists(currentPath));
            if (!exists) missing++;

            if (pairs.Count < MaxPreviewRows)
            {
                pairs.Add(new UndoPreviewPair(
                    Path.GetFileName(currentPath),
                    Path.GetFileName(originalPath),
                    exists ? okLabel : missingLabel,
                    !exists));
            }
        }

        return (pairs, steps.Count, missing);
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not HistoryRow row)
        {
            MacAlert.Show(Loc.T("SelectHistoryFirst"), Loc.T("UndoTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var entry = row.Entry;
        var count = _lastPreviewTotal > 0 ? _lastPreviewTotal : entry.Succeeded;
        var confirmMsg = string.Format(TryLoc("UndoConfirmMessage", "Restore {0} file(s) from this run back to their previous names?"), count);
        if (_lastPreviewMissing > 0)
        {
            confirmMsg += "\n\n" + string.Format(
                TryLoc("UndoConfirmMissingWarning", "Note: {0} file(s) appear moved or renamed outside the app and may be skipped."),
                _lastPreviewMissing);
        }

        var owner = Window.GetWindow(this);
        var confirm = owner is not null
            ? MacAlert.Show(owner, confirmMsg, TryLoc("UndoConfirmTitle", Loc.T("UndoTitle")), MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MacAlert.Show(confirmMsg, TryLoc("UndoConfirmTitle", Loc.T("UndoTitle")), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _undo.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => new UndoService(_fs).UndoJournal(entry.JournalPath));
            var message = Loc.F("UndoMessage", result.Undone, result.Total)
                + (result.Failed > 0 ? Loc.F("UndoPartial", result.Failed) : string.Empty);

            if (owner is not null) MacAlert.Show(owner, message, Loc.T("UndoTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            else MacAlert.Show(message, Loc.T("UndoTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            Refresh();
        }
        finally
        {
            _undo.IsEnabled = _list.SelectedItem is HistoryRow;
        }
    }

    private static DataTemplate Parse(string xaml) => (DataTemplate)System.Windows.Markup.XamlReader.Parse(xaml);

    private static DataTemplate BuildRowTemplate() => Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <DockPanel Margin="0,3">
            <TextBlock x:Name="State" DockPanel.Dock="Right" Text="{Binding StatusText}" FontSize="11.5" Margin="12,0,0,0" VerticalAlignment="Center"
                       Foreground="{Binding RelativeSource={RelativeSource AncestorType=ListBoxItem}, Path=Foreground}" />
            <StackPanel>
              <TextBlock Text="{Binding Title}" TextTrimming="CharacterEllipsis"
                         Foreground="{Binding RelativeSource={RelativeSource AncestorType=ListBoxItem}, Path=Foreground}" />
              <TextBlock Text="{Binding Detail}" FontSize="11.5" Opacity="0.75" TextTrimming="CharacterEllipsis"
                         Foreground="{Binding RelativeSource={RelativeSource AncestorType=ListBoxItem}, Path=Foreground}" />
            </StackPanel>
          </DockPanel>
          <DataTemplate.Triggers>
            <DataTrigger Binding="{Binding HasFailures}" Value="True">
              <Setter TargetName="State" Property="FontWeight" Value="SemiBold" />
              <Setter TargetName="State" Property="Text" Value="{Binding StatusText, StringFormat=⚠ {0}}" />
            </DataTrigger>
          </DataTemplate.Triggers>
        </DataTemplate>
        """);

    private static DataTemplate BuildGroupHeaderTemplate() => Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <TextBlock Text="{Binding Name}" FontSize="11" FontFamily="{DynamicResource Mac.FontSemi}"
                     Foreground="{DynamicResource Mac.Label3}" Margin="16,10,12,4" />
        </DataTemplate>
        """);

    private static DataTemplate BuildPairTemplate() => Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <StackPanel Margin="0,2">
            <TextBlock Text="{Binding CurrentName}" FontSize="11.5" Opacity="0.75" TextTrimming="CharacterEllipsis" ToolTip="{Binding CurrentName}"
                       Foreground="{Binding RelativeSource={RelativeSource AncestorType=ListBoxItem}, Path=Foreground}" />
            <TextBlock Text="{Binding Target}" FontSize="12" TextTrimming="CharacterEllipsis" ToolTip="{Binding OriginalName}"
                       Foreground="{Binding RelativeSource={RelativeSource AncestorType=ListBoxItem}, Path=Foreground}" />
            <TextBlock x:Name="Missing" Text="{Binding StateText}" FontSize="11" Visibility="Collapsed" Foreground="{DynamicResource Mac.Bad}" />
          </StackPanel>
          <DataTemplate.Triggers>
            <DataTrigger Binding="{Binding IsMissing}" Value="True">
              <Setter TargetName="Missing" Property="Visibility" Value="Visible" />
            </DataTrigger>
          </DataTemplate.Triggers>
        </DataTemplate>
        """);
}
