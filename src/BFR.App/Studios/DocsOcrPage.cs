namespace BFR.App.Studios;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BFR.App.Controls;
using BFR.Core.Licensing;
using BFR.Documents.Abstractions;
using BFR.Documents.Ocr;
using BFR.Documents.Office;
using BFR.Documents.Pdf;
using BFR.Presentation.Studios;
using BFR.Presentation.Tools;
using Microsoft.Win32;

/// <summary>
/// Bàn làm việc Docs &amp; OCR v3.0:
/// 1. Convert to PDF (Hybrid Office/LibreOffice indicator + pure-.NET fallback notice).
/// 2. Office Privacy Cleaner (Author, LastModifiedBy, Company, Comments, Revisions before/after).
/// 3. Spreadsheet (XLSX ↔ CSV / JSON).
/// 4. OCR Workbench (Side-by-side Split View: ảnh/trang PDF gốc ↔ văn bản nhận diện chỉnh sửa được, kiểm tra gói ngôn ngữ Windows + ms-settings:regionlanguage).
/// </summary>
public sealed class DocsOcrPage : PageBase
{
    private readonly DocsOcrStudioViewModel _vm;
    private readonly PdfThumbnailCache _previewCache = new();
    private readonly ContinueWithBar _continueBar = new();
    private readonly Action<ProFeature?> _requestUpgrade;
    private readonly Action<ContinueWithAction, IReadOnlyList<string>> _onContinueWith;

    private readonly Dictionary<DocsOcrMode, RadioButton> _modeTabs = new();
    private readonly TextBlock _fileSummary = new();
    private readonly Grid _workspaceHost = new();
    private readonly ProgressBar _progressBar = new() { Height = 4, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly Button _cancelBtn = new();
    private readonly TextBlock _statusText = new();

    private CancellationTokenSource? _opCts;

    // ConvertToPdf controls
    private CheckBox? _landscapeCheck;

    // PrivacyCleaner controls
    private StackPanel? _privacyReportHost;
    private CheckBox? _removeCommentsCheck;
    private CheckBox? _removeRevisionsCheck;

    // Spreadsheet controls
    private SpreadsheetTargetFormat _spreadsheetTarget = SpreadsheetTargetFormat.Csv;
    private TextBox? _sheetNameBox;

    // OCR controls
    private ComboBox? _ocrLangCombo;
    private Border? _missingLangBanner;
    private Image? _ocrSourcePreview;
    private TextBox? _ocrTextBox;

    public DocsOcrPage(
        LicenseService license,
        string journalDir,
        Action<ProFeature?> requestUpgrade,
        Action<ContinueWithAction, IReadOnlyList<string>> onContinueWith)
    {
        _requestUpgrade = requestUpgrade;
        _onContinueWith = onContinueWith;
        _vm = new DocsOcrStudioViewModel(license, journalDir: journalDir);
        _continueBar.ActionSelected += (action, paths) => _onContinueWith(action, paths);

        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                await LoadFilesAsync(files);
            }
            e.Handled = true;
        };

        Content = BuildLayout();
        BuildModeView();
    }

    public void SetMode(string? mode)
    {
        var parsed = mode?.ToLowerInvariant() switch
        {
            "privacy" or "clean" => DocsOcrMode.PrivacyCleaner,
            "spreadsheet" or "xlsx" => DocsOcrMode.SpreadsheetData,
            "ocr" => DocsOcrMode.OcrWorkbench,
            _ => DocsOcrMode.ConvertToPdf
        };
        _vm.Mode = parsed;
        if (_modeTabs.TryGetValue(parsed, out var rb)) rb.IsChecked = true;
        BuildModeView();
    }

    public async Task LoadFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count == 0) return;
        _vm.Clear();
        _vm.AddFiles(paths);
        UpdateFileSummary();
        await RefreshPreviewForCurrentModeAsync();
    }

    public override void Shutdown()
    {
        _opCts?.Cancel();
        _opCts?.Dispose();
        _previewCache.Dispose();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20, 14, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Mode bar + actions
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // File summary bar
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Mode content
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Progress + ContinueWithBar

        // Row 0: Mode tabs + file buttons
        var topDock = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
        var fileButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var addBtn = new Button { Content = TryLoc("AddFilesButton", "Add Files"), Style = S("BfrPrimaryButton"), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) };
        addBtn.Click += async (_, _) => await BrowseFilesAsync();
        var clearBtn = new Button { Content = TryLoc("ClearListButton", "Clear"), Style = S("BfrGhostButton"), Padding = new Thickness(10, 8, 10, 8) };
        clearBtn.Click += (_, _) =>
        {
            _vm.Clear();
            _continueBar.Clear();
            UpdateFileSummary();
            BuildModeView();
        };
        fileButtons.Children.Add(addBtn);
        fileButtons.Children.Add(clearBtn);
        DockPanel.SetDock(fileButtons, Dock.Right);
        topDock.Children.Add(fileButtons);

        var modeTrack = new Border { Style = S("BfrSegmentedTrack"), VerticalAlignment = VerticalAlignment.Center };
        var modeStack = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (mode, label) in new[]
        {
            (DocsOcrMode.ConvertToPdf, TryLoc("DocsModeConvertPdf", "Convert to PDF")),
            (DocsOcrMode.PrivacyCleaner, TryLoc("DocsModePrivacy", "Office Privacy Cleaner")),
            (DocsOcrMode.SpreadsheetData, TryLoc("DocsModeSpreadsheet", "Spreadsheet")),
            (DocsOcrMode.OcrWorkbench, TryLoc("DocsModeOcr", "OCR Workbench"))
        })
        {
            var captured = mode;
            var rb = new RadioButton
            {
                Content = label,
                GroupName = "DocsOcrModes",
                Style = S("BfrSegmentedItem"),
                IsChecked = captured == _vm.Mode
            };
            rb.Checked += async (_, _) =>
            {
                _vm.Mode = captured;
                BuildModeView();
                await RefreshPreviewForCurrentModeAsync();
            };
            _modeTabs[captured] = rb;
            modeStack.Children.Add(rb);
        }
        modeTrack.Child = modeStack;
        topDock.Children.Add(modeTrack);
        Grid.SetRow(topDock, 0);
        root.Children.Add(topDock);

        // Row 1: Loaded file summary
        _fileSummary.Style = S("BfrCaptionText");
        _fileSummary.Foreground = Res<Brush>("BfrFg2");
        UpdateFileSummary();
        var fileBar = new Border
        {
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = Res<Brush>("BfrLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 0, 0, 12),
            Child = _fileSummary
        };
        Grid.SetRow(fileBar, 1);
        root.Children.Add(fileBar);

        // Row 2: Workspace host
        Grid.SetRow(_workspaceHost, 2);
        root.Children.Add(_workspaceHost);

        // Row 3: Progress + ContinueWithBar
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var statusRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        _cancelBtn.Content = TryLoc("ConfirmCancel", "Cancel");
        _cancelBtn.Style = S("BfrGhostButton");
        _cancelBtn.Visibility = Visibility.Collapsed;
        _cancelBtn.Click += (_, _) => _opCts?.Cancel();
        DockPanel.SetDock(_cancelBtn, Dock.Right);
        statusRow.Children.Add(_cancelBtn);
        _statusText.Style = S("BfrCaptionText");
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        statusRow.Children.Add(_statusText);
        bottom.Children.Add(statusRow);
        bottom.Children.Add(_progressBar);
        bottom.Children.Add(_continueBar);
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);

        return root;
    }

    private void BuildModeView()
    {
        _workspaceHost.Children.Clear();
        switch (_vm.Mode)
        {
            case DocsOcrMode.ConvertToPdf:
                _workspaceHost.Children.Add(BuildConvertView());
                break;
            case DocsOcrMode.PrivacyCleaner:
                _workspaceHost.Children.Add(BuildPrivacyView());
                break;
            case DocsOcrMode.SpreadsheetData:
                _workspaceHost.Children.Add(BuildSpreadsheetView());
                break;
            case DocsOcrMode.OcrWorkbench:
                _workspaceHost.Children.Add(BuildOcrView());
                break;
        }
    }

    private UIElement BuildConvertView()
    {
        var card = new Border
        {
            Background = Res<Brush>("BfrGlassFill"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(24)
        };

        var stack = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(new TextBlock { Text = TryLoc("DocsConvertTitle", "Convert Office, Markdown, Text & CSV to PDF"), Style = S("BfrHeadingText"), Margin = new Thickness(0, 0, 0, 8) });

        // Hybrid Office / LibreOffice indicator
        var bridge = _vm.BridgeStatus;
        var hasHighFidelity = bridge.HasLibreOffice || bridge.HasMicrosoftOffice;
        var engineLabel = bridge.DetectedEngine switch
        {
            OfficePdfEngineKind.LibreOffice => $"LibreOffice Headless ({Path.GetFileName(bridge.ExecutablePath)})",
            OfficePdfEngineKind.MicrosoftOffice => "Microsoft Office Local Bridge",
            _ => TryLoc("DocsPureNetFallback", "Pure-.NET Offline Renderer (Install LibreOffice or MS Office for 100% layout fidelity)")
        };
        stack.Children.Add(new Border
        {
            Background = Res<Brush>(hasHighFidelity ? "BfrOkSoft" : "BfrWarnSoft"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 14),
            Child = new TextBlock { Text = engineLabel, Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg") }
        });

        _landscapeCheck = new CheckBox
        {
            Content = TryLoc("DocsLandscapeOption", "Landscape orientation (recommended for wide CSV tables)"),
            Foreground = Res<Brush>("BfrFg"),
            Margin = new Thickness(0, 0, 0, 16)
        };
        stack.Children.Add(_landscapeCheck);

        var runBtn = new Button { Content = TryLoc("DocsConvertRun", "Convert to PDF"), Style = S("BfrPrimaryButton") };
        runBtn.Click += async (_, _) => await RunConvertToPdfAsync();
        stack.Children.Add(runBtn);

        card.Child = stack;
        return card;
    }

    private UIElement BuildPrivacyView()
    {
        var card = new Border
        {
            Background = Res<Brush>("BfrGlassFill"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(24)
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = TryLoc("DocsPrivacyTitle", "Office Privacy Cleaner (.docx, .xlsx, .pptx)"), Style = S("BfrHeadingText"), Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(new TextBlock
        {
            Text = TryLoc("DocsPrivacyDesc", "Inspect and permanently strip hidden Author, Company, LastModifiedBy, Comments, and Tracked Revisions offline."),
            Style = S("BfrCaptionText"),
            Margin = new Thickness(0, 0, 0, 14)
        });

        _removeCommentsCheck = new CheckBox { Content = TryLoc("DocsRemoveComments", "Remove hidden comments"), IsChecked = true, Foreground = Res<Brush>("BfrFg"), Margin = new Thickness(0, 0, 0, 6) };
        _removeRevisionsCheck = new CheckBox { Content = TryLoc("DocsRemoveRevisions", "Accept and strip tracked revisions"), IsChecked = true, Foreground = Res<Brush>("BfrFg"), Margin = new Thickness(0, 0, 0, 14) };
        stack.Children.Add(_removeCommentsCheck);
        stack.Children.Add(_removeRevisionsCheck);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        var inspectBtn = new Button { Content = TryLoc("DocsInspectMetadata", "Inspect Hidden Data"), Style = S("BfrSecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        inspectBtn.Click += async (_, _) => await RunInspectPrivacyAsync();
        var cleanBtn = new Button { Content = TryLoc("DocsCleanPrivacyRun", "Clean & Save Copy"), Style = S("BfrPrimaryButton") };
        cleanBtn.Click += async (_, _) => await RunCleanPrivacyAsync();
        btns.Children.Add(inspectBtn);
        btns.Children.Add(cleanBtn);
        stack.Children.Add(btns);

        _privacyReportHost = new StackPanel();
        RenderPrivacyReport();
        stack.Children.Add(_privacyReportHost);

        card.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
        return card;
    }

    private void RenderPrivacyReport()
    {
        if (_privacyReportHost is null) return;
        _privacyReportHost.Children.Clear();

        if (_vm.LastCleanReport is { } rep)
        {
            AddSnapshotCard("Before Cleaning", rep.Before);
            AddSnapshotCard($"After Cleaning ({rep.TotalItemsRemoved} items removed)", rep.After);
        }
        else if (_vm.InspectedMetadata is { } snap)
        {
            AddSnapshotCard("Detected Hidden Metadata", snap);
        }
    }

    private void AddSnapshotCard(string title, OpenXmlMetadataSnapshot s)
    {
        if (_privacyReportHost is null) return;
        var text = $"Author: {s.Author ?? "—"} · ModifiedBy: {s.LastModifiedBy ?? "—"} · Company: {s.Company ?? "—"}\n" +
                   $"Comments: {s.CommentsCount} · Revisions: {s.RevisionsCount} · Custom Properties: {s.CustomPropertiesCount}";
        _privacyReportHost.Children.Add(new Border
        {
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = Res<Brush>("BfrLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrAccFg"), Margin = new Thickness(0, 0, 0, 4) },
                    new TextBlock { Text = text, Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg") }
                }
            }
        });
    }

    private UIElement BuildSpreadsheetView()
    {
        var card = new Border
        {
            Background = Res<Brush>("BfrGlassFill"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(24)
        };

        var stack = new StackPanel { MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(new TextBlock { Text = TryLoc("DocsSpreadsheetTitle", "Spreadsheet Converter (XLSX ↔ CSV / JSON)"), Style = S("BfrHeadingText"), Margin = new Thickness(0, 0, 0, 12) });

        foreach (var (fmt, label) in new[]
        {
            (SpreadsheetTargetFormat.Csv, "XLSX → CSV (Formula-injection safe)"),
            (SpreadsheetTargetFormat.Json, "XLSX → JSON (Pro)"),
            (SpreadsheetTargetFormat.Xlsx, "CSV → XLSX")
        })
        {
            var captured = fmt;
            var rb = new RadioButton
            {
                Content = label,
                GroupName = "SpreadsheetFmt",
                IsChecked = _spreadsheetTarget == captured,
                Foreground = Res<Brush>("BfrFg"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            rb.Checked += (_, _) => _spreadsheetTarget = captured;
            stack.Children.Add(rb);
        }

        stack.Children.Add(new TextBlock { Text = TryLoc("DocsSheetName", "Sheet name (optional)"), Style = S("BfrCaptionText"), Margin = new Thickness(0, 8, 0, 4) });
        _sheetNameBox = new TextBox { Style = S("BfrTextField"), Tag = "Sheet1", Margin = new Thickness(0, 0, 0, 16) };
        stack.Children.Add(_sheetNameBox);

        var runBtn = new Button { Content = TryLoc("DocsSpreadsheetRun", "Convert Spreadsheet"), Style = S("BfrPrimaryButton") };
        runBtn.Click += async (_, _) => await RunSpreadsheetAsync();
        stack.Children.Add(runBtn);

        card.Child = stack;
        return card;
    }

    private UIElement BuildOcrView()
    {
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left: Source image/PDF preview
        var leftCard = new Border
        {
            Background = Res<Brush>("BfrGlassFill"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 10, 0)
        };
        _ocrSourcePreview = new Image { Stretch = Stretch.Uniform };
        leftCard.Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _ocrSourcePreview
        };
        Grid.SetColumn(leftCard, 0);
        split.Children.Add(leftCard);

        // Right: Controls + Language Pack check + Editable Extracted Text
        var rightCard = new Border
        {
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14)
        };
        var rightDock = new DockPanel { LastChildFill = true };

        var controls = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var topRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

        _ocrLangCombo = new ComboBox { Width = 210, Margin = new Thickness(0, 0, 8, 4) };
        foreach (var lang in _vm.AvailableLanguages)
        {
            _ocrLangCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{lang.DisplayName} ({lang.LanguageTag})",
                Tag = lang.LanguageTag,
                IsSelected = string.Equals(lang.LanguageTag, _vm.SelectedLanguageTag, StringComparison.OrdinalIgnoreCase)
            });
        }
        _ocrLangCombo.SelectionChanged += (_, _) =>
        {
            if (_ocrLangCombo.SelectedItem is ComboBoxItem { Tag: string tag })
            {
                _vm.SelectedLanguageTag = tag;
                UpdateOcrLanguageBanner();
            }
        };
        topRow.Children.Add(_ocrLangCombo);

        var runOcrBtn = new Button { Content = TryLoc("OcrRunButton", "Run Offline OCR"), Style = S("BfrPrimaryButton"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 4) };
        runOcrBtn.Click += async (_, _) => await RunOcrAsync(OcrOutputFormat.Text, saveToFile: false);
        topRow.Children.Add(runOcrBtn);

        var searchablePdfBtn = new Button { Content = TryLoc("OcrSearchablePdfButton", "Save Searchable PDF"), Style = S("BfrSecondaryButton"), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 4) };
        searchablePdfBtn.Click += async (_, _) => await RunOcrAsync(OcrOutputFormat.SearchablePdf, saveToFile: true);
        topRow.Children.Add(searchablePdfBtn);

        controls.Children.Add(topRow);

        // Missing Language Pack banner (Edge Case #7)
        var missingDock = new DockPanel { LastChildFill = true };
        var openSettingsBtn = new Button
        {
            Content = TryLoc("EdgeOpenLangSettings", "Open Windows Language Settings"),
            Style = S("BfrSecondaryButton"),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(8, 0, 0, 0)
        };
        openSettingsBtn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }); }
            catch { }
        };
        DockPanel.SetDock(openSettingsBtn, Dock.Right);
        missingDock.Children.Add(openSettingsBtn);
        missingDock.Children.Add(new TextBlock
        {
            Text = TryLoc("EdgeMissingOcrLangMsg", "Selected Windows OCR language pack is not installed."),
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>("BfrFg"),
            VerticalAlignment = VerticalAlignment.Center
        });
        _missingLangBanner = new Border
        {
            Background = Res<Brush>("BfrWarnSoft"),
            BorderBrush = Res<Brush>("BfrWarn"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 8),
            Child = missingDock
        };
        UpdateOcrLanguageBanner();
        controls.Children.Add(_missingLangBanner);

        DockPanel.SetDock(controls, Dock.Top);
        rightDock.Children.Add(controls);

        // Bottom export row
        var exportRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var copyBtn = new Button { Content = TryLoc("OcrCopyText", "Copy Text"), Style = S("BfrSecondaryButton"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 6, 0) };
        copyBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_vm.ExtractedText)) Clipboard.SetText(_vm.ExtractedText);
        };
        var saveTxtBtn = new Button { Content = "Save .txt", Style = S("BfrSecondaryButton"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 6, 0) };
        saveTxtBtn.Click += async (_, _) => await ExportOcrTextAsync(".txt", "Text (*.txt)|*.txt");
        var saveMdBtn = new Button { Content = "Save .md", Style = S("BfrSecondaryButton"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 6, 0) };
        saveMdBtn.Click += async (_, _) => await ExportOcrTextAsync(".md", "Markdown (*.md)|*.md");
        var saveDocxBtn = new Button { Content = "Save .docx", Style = S("BfrSecondaryButton"), Padding = new Thickness(10, 6, 10, 6) };
        saveDocxBtn.Click += async (_, _) => await ExportOcrTextAsync(".docx", "Word Document (*.docx)|*.docx");

        exportRow.Children.Add(copyBtn);
        exportRow.Children.Add(saveTxtBtn);
        exportRow.Children.Add(saveMdBtn);
        exportRow.Children.Add(saveDocxBtn);
        DockPanel.SetDock(exportRow, Dock.Bottom);
        rightDock.Children.Add(exportRow);

        _ocrTextBox = new TextBox
        {
            Text = _vm.ExtractedText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Res<Brush>("BfrSurface1"),
            Foreground = Res<Brush>("BfrFg"),
            BorderBrush = Res<Brush>("BfrLine"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            FontFamily = Res<FontFamily>("BfrFontBody"),
            FontSize = 13.5
        };
        _ocrTextBox.TextChanged += (_, _) => _vm.ExtractedText = _ocrTextBox.Text;
        rightDock.Children.Add(_ocrTextBox);

        rightCard.Child = rightDock;
        Grid.SetColumn(rightCard, 1);
        split.Children.Add(rightCard);

        return split;
    }

    private void UpdateOcrLanguageBanner()
    {
        if (_missingLangBanner is null) return;
        _missingLangBanner.Visibility = _vm.IsSelectedLanguageSupported && _vm.AvailableLanguages.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async Task RefreshPreviewForCurrentModeAsync()
    {
        if (_vm.Mode != DocsOcrMode.OcrWorkbench || _ocrSourcePreview is null || _vm.InputFiles.Count == 0)
            return;

        var first = _vm.InputFiles[0];
        if (!File.Exists(first)) return;

        try
        {
            if (Path.GetExtension(first).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                _ocrSourcePreview.Source = await _previewCache.GetThumbnailBitmapAsync(first, 1, 720);
            }
            else
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(first, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                _ocrSourcePreview.Source = bmp;
            }
        }
        catch
        {
            _ocrSourcePreview.Source = null;
        }
    }

    private async Task BrowseFilesAsync()
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Documents & Images (*.docx;*.xlsx;*.pptx;*.md;*.txt;*.csv;*.pdf;*.png;*.jpg;*.jpeg;*.webp)|*.docx;*.xlsx;*.pptx;*.md;*.txt;*.csv;*.pdf;*.png;*.jpg;*.jpeg;*.webp|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            await LoadFilesAsync(dlg.FileNames);
    }

    private async Task RunConvertToPdfAsync()
    {
        if (_vm.InputFiles.Count == 0) return;
        var first = _vm.InputFiles[0];
        var dlg = new SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(first) + ".pdf"
        };
        if (dlg.ShowDialog() != true) return;

        await ExecuteAsync(async ct =>
        {
            var res = await _vm.ExecuteConvertToPdfAsync(dlg.FileName, _landscapeCheck?.IsChecked == true, ct);
            HandleResult(res);
        });
    }

    private async Task RunInspectPrivacyAsync()
    {
        if (_vm.InputFiles.Count == 0) return;
        await ExecuteAsync(async ct =>
        {
            var res = await _vm.InspectPrivacyAsync(ct);
            _statusText.Text = res.Message ?? _vm.StatusMessage;
            RenderPrivacyReport();
        });
    }

    private async Task RunCleanPrivacyAsync()
    {
        if (_vm.InputFiles.Count == 0) return;
        var first = _vm.InputFiles[0];
        var ext = Path.GetExtension(first);
        var dlg = new SaveFileDialog
        {
            Filter = $"Office Document (*{ext})|*{ext}",
            FileName = Path.GetFileNameWithoutExtension(first) + "_cleaned" + ext
        };
        if (dlg.ShowDialog() != true) return;

        await ExecuteAsync(async ct =>
        {
            var opts = new OpenXmlCleanOptions(
                RemoveComments: _removeCommentsCheck?.IsChecked == true,
                AcceptAndStripRevisions: _removeRevisionsCheck?.IsChecked == true);
            var res = await _vm.CleanPrivacyAsync(dlg.FileName, opts, ct);
            _statusText.Text = res.Message ?? _vm.StatusMessage;
            RenderPrivacyReport();
            if (res.Success && _vm.LastOutputPaths.Count > 0)
                _continueBar.ShowResult(_statusText.Text, _vm.LastOutputPaths);
            else if (_vm.ShowUpgradePrompt)
                _requestUpgrade(_vm.UpgradeRequiredFeature);
        });
    }

    private async Task RunSpreadsheetAsync()
    {
        if (_vm.InputFiles.Count == 0) return;
        var first = _vm.InputFiles[0];
        var (ext, filter) = _spreadsheetTarget switch
        {
            SpreadsheetTargetFormat.Json => (".json", "JSON (*.json)|*.json"),
            SpreadsheetTargetFormat.Xlsx => (".xlsx", "Excel (*.xlsx)|*.xlsx"),
            _ => (".csv", "CSV (*.csv)|*.csv")
        };
        var dlg = new SaveFileDialog { Filter = filter, FileName = Path.GetFileNameWithoutExtension(first) + ext };
        if (dlg.ShowDialog() != true) return;

        await ExecuteAsync(async ct =>
        {
            var res = await _vm.ExecuteSpreadsheetConvertAsync(dlg.FileName, _spreadsheetTarget, _sheetNameBox?.Text, ct);
            HandleResult(res);
        });
    }

    private async Task RunOcrAsync(OcrOutputFormat format, bool saveToFile)
    {
        if (_vm.InputFiles.Count == 0) return;
        string? outPath = null;
        if (saveToFile)
        {
            var first = _vm.InputFiles[0];
            var dlg = new SaveFileDialog
            {
                Filter = "Searchable PDF (*.pdf)|*.pdf",
                FileName = Path.GetFileNameWithoutExtension(first) + "_ocr.pdf"
            };
            if (dlg.ShowDialog() != true) return;
            outPath = dlg.FileName;
        }

        await ExecuteAsync(async ct =>
        {
            var res = await _vm.RunOcrAsync(outPath, format, ct: ct);
            _statusText.Text = res.Message ?? _vm.StatusMessage;
            if (_ocrTextBox is not null) _ocrTextBox.Text = _vm.ExtractedText;
            if (res.Success && _vm.LastOutputPaths.Count > 0)
                _continueBar.ShowResult(_statusText.Text, _vm.LastOutputPaths);
            else if (res.ErrorCode == DocumentErrorCode.MissingOcrLanguagePack)
                UpdateOcrLanguageBanner();
        });
    }

    private async Task ExportOcrTextAsync(string ext, string filter)
    {
        var baseName = _vm.InputFiles.Count > 0
            ? Path.GetFileNameWithoutExtension(_vm.InputFiles[0])
            : "ocr_result";
        var dlg = new SaveFileDialog { Filter = filter, FileName = baseName + ext };
        if (dlg.ShowDialog() != true) return;

        await ExecuteAsync(async ct =>
        {
            var res = await _vm.ExportExtractedTextAsync(dlg.FileName, ct);
            HandleResult(res);
        });
    }

    private void HandleResult(DocumentResult res)
    {
        _statusText.Text = res.Message ?? _vm.StatusMessage;
        if (res.Success && _vm.LastOutputPaths.Count > 0)
        {
            _continueBar.ShowResult(_statusText.Text, _vm.LastOutputPaths);
        }
        else if (_vm.ShowUpgradePrompt)
        {
            _requestUpgrade(_vm.UpgradeRequiredFeature);
        }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> work)
    {
        _opCts?.Cancel();
        _opCts = new CancellationTokenSource();
        _progressBar.Visibility = Visibility.Visible;
        _progressBar.IsIndeterminate = true;
        _cancelBtn.Visibility = Visibility.Visible;
        try
        {
            await work(_opCts.Token);
        }
        catch (OperationCanceledException)
        {
            _statusText.Text = TryLoc("ToastCancelled", "Cancelled — no changes saved.");
        }
        finally
        {
            _progressBar.Visibility = Visibility.Collapsed;
            _cancelBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateFileSummary()
    {
        _fileSummary.Text = _vm.InputFiles.Count == 0
            ? TryLoc("DocsNoFileLoaded", "Drop Word, Excel, PowerPoint, Markdown, CSV, PDF, or image files here.")
            : $"{_vm.InputFiles.Count} file(s): {string.Join(", ", _vm.InputFiles.Select(Path.GetFileName))}";
    }

    private static Style S(string key) => (Style)Application.Current.FindResource(key);
    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    private static T Res<T>(string key) where T : class => (T)Application.Current.FindResource(key);
    private static string TryLoc(string key, string fallback)
    {
        var val = Application.Current?.TryFindResource(key) as string;
        return string.IsNullOrEmpty(val) ? fallback : val;
    }
}
