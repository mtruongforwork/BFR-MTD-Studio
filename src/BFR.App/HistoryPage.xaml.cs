namespace BFR.App;

using BFR.Core;
using BFR.Core.Licensing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

public partial class HistoryPage : PageBase
{
    private const int MaxPreviewRows = 500;

    /// <summary>Trang bị rời đi: cửa sổ chính tính lại xem trước (tên tệp có thể đã được hoàn tác).</summary>
    public event EventHandler? Left;

    public override void OnShown() => Refresh();

    public override void OnHidden()
    {
        _previewCts?.Cancel();
        Left?.Invoke(this, EventArgs.Empty);
    }

    private sealed class HistoryRow
    {
        public HistoryRow(HistoryEntry entry)
        {
            Entry = entry;
            TimeDisplay = FormatLocalAndRelative(entry.StartedUtc);
        }

        public HistoryEntry Entry { get; }
        public string Id => Entry.Id;
        public string TimeDisplay { get; }
        public int Total => Entry.Total;
        public int Succeeded => Entry.Succeeded;
        public int Failed => Entry.Failed;
        public string StatusText => Entry.StatusText;
        public string PresetText => Entry.PresetText;
    }

    private sealed record UndoPreviewPair(string CurrentName, string Arrow, string OriginalName, string StateText, bool IsMissing);

    private readonly HistoryStore _history;
    private readonly IFileSystem _fs;
    private readonly LicenseService _license;
    private CancellationTokenSource? _previewCts;
    private int _lastPreviewTotal;
    private int _lastPreviewMissing;

    public HistoryPage(HistoryStore history, IFileSystem fs, LicenseService? license = null)
    {
        InitializeComponent();
        _history = history;
        _fs = fs;
        _license = license ?? new LicenseService(new LicenseStore(new RealFileSystem()), trialEnabled: false);

        HistoryGrid.Columns[0].Header = Loc.T("HistoryColId");
        HistoryGrid.Columns[1].Header = Loc.T("HistoryColTime");
        HistoryGrid.Columns[2].Header = Loc.T("HistoryColTotal");
        HistoryGrid.Columns[3].Header = Loc.T("HistoryColOk");
        HistoryGrid.Columns[4].Header = Loc.T("HistoryColFailed");
        HistoryGrid.Columns[5].Header = Loc.T("HistoryColStatus");
        HistoryGrid.Columns[6].Header = Loc.T("HistoryColPreset");

        ColCurrentName.Header = TryLoc("HistoryPreviewCurrent", "Current name");
        ColOriginalName.Header = TryLoc("HistoryPreviewOriginal", "Will restore to");
        ColState.Header = TryLoc("HistoryColStatus", "Status");

        Refresh();
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

        var entries = _history.LoadAll()
            .Take(_license.MaxUndoEntries == int.MaxValue ? int.MaxValue : _license.MaxUndoEntries)
            .Select(e => new HistoryRow(e))
            .ToList();

        HistoryGrid.ItemsSource = entries;
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewDrawer.Visibility = Visibility.Collapsed;
        PreviewPairsGrid.ItemsSource = null;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void ClosePreview_Click(object sender, RoutedEventArgs e)
    {
        _previewCts?.Cancel();
        PreviewDrawer.Visibility = Visibility.Collapsed;
    }

    private async void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;

        if (HistoryGrid.SelectedItem is not HistoryRow row || string.IsNullOrWhiteSpace(row.Entry.JournalPath))
        {
            PreviewDrawer.Visibility = Visibility.Collapsed;
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        var token = cts.Token;

        PreviewDrawer.Visibility = Visibility.Visible;
        PreviewTitle.Text = TryLoc("HistoryPreviewTitle", "Undo preview");
        PreviewSummary.Text = TryLoc("HistoryPreviewLoading", "Loading journal…");
        PreviewPairsGrid.ItemsSource = null;

        try
        {
            var journalPath = row.Entry.JournalPath;
            var (pairs, total, missing) = await Task.Run(() => LoadPreviewPairs(journalPath, token), token);
            if (token.IsCancellationRequested) return;

            _lastPreviewTotal = total;
            _lastPreviewMissing = missing;
            PreviewPairsGrid.ItemsSource = pairs;

            var shownNote = total > MaxPreviewRows
                ? string.Format(TryLoc("HistoryPreviewCapped", "Showing first {0} of {1} files"), MaxPreviewRows, total)
                : string.Format(TryLoc("HistoryPreviewCount", "{0} files"), total);
            if (missing > 0)
            {
                shownNote += " · " + string.Format(TryLoc("HistoryPreviewMissingCount", "{0} missing/moved"), missing);
            }
            PreviewSummary.Text = shownNote;
        }
        catch (OperationCanceledException)
        {
            // Đã chuyển sang dòng khác.
        }
        catch
        {
            if (!token.IsCancellationRequested)
                PreviewSummary.Text = TryLoc("HistoryPreviewUnavailable", "Journal details unavailable.");
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
                    "→",
                    Path.GetFileName(originalPath),
                    exists ? okLabel : missingLabel,
                    !exists));
            }
        }

        return (pairs, steps.Count, missing);
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not HistoryRow row)
        {
            MessageBox.Show(Loc.T("SelectHistoryFirst"), Loc.T("UndoTitle"),
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
            ? MessageBox.Show(owner, confirmMsg, TryLoc("UndoConfirmTitle", Loc.T("UndoTitle")), MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(confirmMsg, TryLoc("UndoConfirmTitle", Loc.T("UndoTitle")), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        UndoButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => new UndoService(_fs).UndoJournal(entry.JournalPath));
            var message = Loc.F("UndoMessage", result.Undone, result.Total)
                + (result.Failed > 0 ? Loc.F("UndoPartial", result.Failed) : string.Empty);

            if (owner is not null) MessageBox.Show(owner, message, Loc.T("UndoTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            else MessageBox.Show(message, Loc.T("UndoTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            Refresh();
        }
        finally
        {
            UndoButton.IsEnabled = true;
        }
    }
}
