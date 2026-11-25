namespace BFR.App.Studios;


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BFR.Core;
using BFR.Core.Ai;
using BFR.Core.Licensing;
using BFR.Media;
using BFR.Presentation.Studios;
using Microsoft.Win32;

/// <summary>
/// Media &amp; Clean Studio (Phase 3–5):
/// - Tab 1: Duplicate &amp; Similar Image Finder (SHA-256 3-tier + Perceptual dHash, Recycle Bin safe delete)
/// - Tab 2: Offline AI Background Removal (Phase 4)
/// - Tab 3: Offline Audio/Video Transcription (Phase 5)
/// </summary>
public sealed class MediaCleanPage : PageBase
{
    private readonly MediaCleanStudioViewModel _vm;
    private readonly LicenseService _license;
    private readonly Action<ProFeature?> _requestUpgrade;
    private readonly Action<IReadOnlyList<string>> _sendToRename;

    private readonly Dictionary<MediaCleanMode, RadioButton> _modeTabs = new();
    private readonly ContentControl _modeHost = new();
    private readonly StackPanel _alertHost = new();
    private readonly Border _processingCard = new();
    private readonly TextBlock _processingTitle = new();
    private readonly TextBlock _processingPercent = new();
    private readonly ProgressBar _progressBar = new() { Height = 6, Minimum = 0, Maximum = 1 };

    private CancellationTokenSource? _opCts;
    private bool _isBusy;

    // Tab 1 state
    private string _scanFolder = string.Empty;
    private bool _includeSubfolders = true;
    private bool _similarImagesMode;
    private int _similarMaxDistance = 6;
    private StackPanel? _groupsHost;
    private TextBlock? _summaryStatsText;
    private Button? _recycleBtn;

    // Tab 2: Offline AI Background Remover state (Phase 4)
    private readonly LocalModelManager _modelManager = new();
    private readonly OnnxBackgroundRemover _bgRemover = new();
    private readonly List<string> _bgInputImages = new();
    private bool _bgUseHdModel;
    private string _bgFillMode = "transparent"; // transparent | white | blue
    private string? _bgLastOutputPath;

    // Tab 3: Offline Audio/Video Transcriber state (Phase 5)
    private readonly OfflineAudioTranscriber _transcriber = new();
    private string? _whisperInputPath;
    private string _whisperModelId = LocalModelCatalog.WhisperTiny.Id;
    private string _whisperLanguage = "auto";
    private string _whisperPreviewTab = "txt"; // txt | srt | vtt
    private TranscriptionResult? _lastTranscript;

    private bool IsVi => string.Equals(Loc.Current, "vi", StringComparison.OrdinalIgnoreCase);

    public MediaCleanPage(
        IFileSystem fs,
        LicenseService license,
        IImageProcessor imageProcessor,
        Action<ProFeature?> requestUpgrade,
        Action<IReadOnlyList<string>> sendToRename)
    {
        _license = license ?? throw new ArgumentNullException(nameof(license));
        _requestUpgrade = requestUpgrade;
        _sendToRename = sendToRename;
        _vm = new MediaCleanStudioViewModel(fs, license, imageProcessor);

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MediaCleanStudioViewModel.Progress))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var p = Math.Clamp(_vm.Progress, 0, 1);
                    if (p > 0)
                    {
                        _progressBar.IsIndeterminate = false;
                        _progressBar.Value = p;
                        _processingPercent.Text = $"{Math.Round(p * 100)}%";
                    }
                });
            }
        };

        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            {
                if (_vm.Mode == MediaCleanMode.RemoveBackground)
                {
                    var imgs = paths.Where(p => File.Exists(p) && ImagePlanner.IsSupportedSource(Path.GetExtension(p))).ToList();
                    if (imgs.Count > 0)
                    {
                        _bgInputImages.Clear();
                        _bgInputImages.AddRange(imgs);
                        _bgLastOutputPath = null;
                        BuildModeView();
                    }
                }
                else if (_vm.Mode == MediaCleanMode.Transcribe)
                {
                    var media = paths.FirstOrDefault(p => File.Exists(p) && OfflineAudioTranscriber.IsSupportedMediaExtension(Path.GetExtension(p)));
                    if (media is not null)
                    {
                        _whisperInputPath = media;
                        _lastTranscript = null;
                        BuildModeView();
                    }
                }
                else
                {
                    var first = paths[0];
                    _scanFolder = Directory.Exists(first) ? first : (Path.GetDirectoryName(first) ?? string.Empty);
                    BuildModeView();
                }
            }
            e.Handled = true;
        };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.Mode)) RaiseNavSubKeyChanged();
        };

        Content = BuildLayout();
        BuildModeView();
    }

    public override string? NavSubKey => _vm.Mode switch
    {
        MediaCleanMode.RemoveBackground => "remove-bg",
        MediaCleanMode.Transcribe => "transcribe",
        _ => "duplicates"
    };

    public void SetMode(string? mode)
    {
        var parsed = mode?.ToLowerInvariant() switch
        {
            "remove-bg" or "rembg" => MediaCleanMode.RemoveBackground,
            "transcribe" or "whisper" => MediaCleanMode.Transcribe,
            _ => MediaCleanMode.Duplicates
        };
        _vm.Mode = parsed;
        if (_modeTabs.TryGetValue(parsed, out var rb)) rb.IsChecked = true;
        BuildModeView();
    }

    public override void Shutdown()
    {
        _opCts?.Cancel();
        _opCts?.Dispose();
        _opCts = null;
        _bgRemover.Dispose();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Mode tabs
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Alerts
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Body
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Processing card

        var topDock = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
        var modeTrack = new Border { Style = S("BfrSegmentedTrack"), VerticalAlignment = VerticalAlignment.Center };
        var modeStack = new WrapPanel();

        var tabs = new (MediaCleanMode Mode, string Label)[]
        {
            (MediaCleanMode.Duplicates, TryLoc("MediaTabDuplicates", "Dọn file trùng & Ảnh giống")),
            (MediaCleanMode.RemoveBackground, TryLoc("MediaTabRemoveBg", "Tách nền AI")),
            (MediaCleanMode.Transcribe, TryLoc("MediaTabTranscribe", "Gỡ băng Audio/Video"))
        };

        foreach (var (m, label) in tabs)
        {
            var captured = m;
            var rb = new RadioButton
            {
                Content = label,
                GroupName = "MediaCleanModes",
                Style = S("BfrSegmentedItem"),
                IsChecked = captured == _vm.Mode
            };
            rb.Checked += (_, _) =>
            {
                _vm.Mode = captured;
                _alertHost.Children.Clear();
                BuildModeView();
            };
            _modeTabs[captured] = rb;
            modeStack.Children.Add(rb);
        }
        modeTrack.Child = modeStack;
        topDock.Children.Add(modeTrack);
        Grid.SetRow(topDock, 0);
        root.Children.Add(topDock);

        _alertHost.Margin = new Thickness(12, 0, 12, 0);
        Grid.SetRow(_alertHost, 1);
        root.Children.Add(_alertHost);

        _modeHost.Margin = new Thickness(12, 0, 12, 12);
        Grid.SetRow(_modeHost, 2);
        root.Children.Add(_modeHost);

        BuildProcessingCard();
        Grid.SetRow(_processingCard, 3);
        root.Children.Add(_processingCard);

        return root;
    }

    private void BuildProcessingCard()
    {
        _processingCard.Background = Res<Brush>("BfrGlassFillStrong");
        _processingCard.BorderBrush = Res<Brush>("BfrAccFg");
        _processingCard.BorderThickness = new Thickness(1);
        _processingCard.CornerRadius = new CornerRadius(12);
        _processingCard.Padding = new Thickness(14, 10, 14, 10);
        _processingCard.Margin = new Thickness(0, 10, 0, 0);
        _processingCard.Visibility = Visibility.Collapsed;

        var sp = new StackPanel();
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var cancelBtn = new Button { Content = IsVi ? "Huỷ" : "Cancel", Style = S("BfrGhostButton"), Padding = new Thickness(10, 2, 10, 2) };
        cancelBtn.Click += (_, _) => _opCts?.Cancel();
        DockPanel.SetDock(cancelBtn, Dock.Right);
        row.Children.Add(cancelBtn);

        _processingPercent.FontWeight = FontWeights.SemiBold;
        _processingPercent.Foreground = Res<Brush>("BfrAccFg");
        _processingPercent.Margin = new Thickness(8, 0, 12, 0);
        _processingPercent.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_processingPercent, Dock.Right);
        row.Children.Add(_processingPercent);

        _processingTitle.FontWeight = FontWeights.SemiBold;
        _processingTitle.Foreground = Res<Brush>("BfrFg");
        _processingTitle.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_processingTitle);

        sp.Children.Add(row);
        sp.Children.Add(_progressBar);
        _processingCard.Child = sp;
    }

    private void BuildModeView()
    {
        _modeHost.Content = _vm.Mode switch
        {
            MediaCleanMode.Duplicates => BuildDuplicatesView(),
            MediaCleanMode.RemoveBackground => BuildRemoveBackgroundView(),
            _ => BuildTranscribeView()
        };
    }

    private UIElement BuildUpcomingPhaseCard(string icon, string title, string desc)
    {
        var card = MakeMainCard();
        var sp = new StackPanel { MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
        sp.Children.Add(new TextBlock
        {
            Text = icon,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 36,
            Foreground = Res<Brush>("BfrAccFg"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        });
        sp.Children.Add(new TextBlock { Text = title, Style = S("BfrHeadingText"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
        sp.Children.Add(new TextBlock { Text = desc, Style = S("BfrBodyText"), Foreground = Res<Brush>("BfrFg2"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        card.Child = sp;
        return card;
    }

    // =========================================================================
    // TAB 2: OFFLINE AI BACKGROUND REMOVER (rembg ONNX)
    // =========================================================================

    private UIElement BuildRemoveBackgroundView()
    {
        var vi = IsVi;
        var isPro = _license.Status.IsPro;
        var activeModel = _bgUseHdModel ? LocalModelCatalog.RembgIsNetHd : LocalModelCatalog.RembgU2NetP;
        var modelInstalled = _modelManager.IsInstalled(activeModel);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 480 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 330 });

        // Left controls
        var leftCard = MakeMainCard();
        var leftDock = new DockPanel { LastChildFill = true };

        var runFooter = new Border
        {
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 12, 16, 12)
        };
        var runBtn = new Button
        {
            Content = TryLoc("MediaBgRunBtn", "Tách nền AI Offline"),
            Style = S("BfrPrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 9, 16, 9),
            IsEnabled = !_isBusy && _bgInputImages.Count > 0
        };
        runBtn.Click += async (_, _) => await RunRemoveBackgroundAsync();
        runFooter.Child = runBtn;
        DockPanel.SetDock(runFooter, Dock.Bottom);
        leftDock.Children.Add(runFooter);

        var leftBody = new StackPanel { Margin = new Thickness(16) };
        leftBody.Children.Add(new TextBlock { Text = vi ? "Tách nền ảnh AI Offline" : "Offline AI Background Remover", Style = S("BfrHeadingText"), Margin = new Thickness(0, 0, 0, 4) });
        leftBody.Children.Add(new TextBlock
        {
            Text = vi ? "Chạy mô hình ONNX (rembg) 100% trên máy — không upload ảnh lên cloud. Model tải tự động 1 lần vào AppData."
                      : "Runs ONNX (rembg) models 100% locally — images never leave your PC. Models download once on demand.",
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>("BfrFg2"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var pickBtn = new Button
        {
            Content = TryLoc("MediaBgSelectImages", "Chọn ảnh (hoặc kéo-thả vào đây)…"),
            Style = S("BfrSecondaryButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };
        pickBtn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Images (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp"
            };
            if (dlg.ShowDialog() == true)
            {
                _bgInputImages.Clear();
                _bgInputImages.AddRange(dlg.FileNames);
                _bgLastOutputPath = null;
                BuildModeView();
            }
        };
        leftBody.Children.Add(pickBtn);

        leftBody.Children.Add(new TextBlock
        {
            Text = _bgInputImages.Count == 0
                ? (vi ? "Chưa chọn ảnh nào." : "No images selected.")
                : (_bgInputImages.Count == 1
                    ? Path.GetFileName(_bgInputImages[0])
                    : (vi ? $"Đã chọn {_bgInputImages.Count} ảnh (chạy hàng loạt cần Pro)" : $"{_bgInputImages.Count} images selected (batch requires Pro)")),
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>("BfrAccFg"),
            Margin = new Thickness(0, 0, 0, 14),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // Model Picker
        leftBody.Children.Add(new TextBlock { Text = vi ? "MÔ HÌNH AI (ON-DEMAND)" : "AI MODEL (ON-DEMAND)", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg3"), Margin = new Thickness(0, 0, 0, 6) });
        leftBody.Children.Add(MakeModeOptionCard(!_bgUseHdModel, "U2Net-P (~4.7 MB)", vi ? "Nhanh, nhẹ cho CPU — Free 1 ảnh/lần" : "Fast & light on CPU — Free 1 image/run", () => { _bgUseHdModel = false; BuildModeView(); }));
        leftBody.Children.Add(MakeModeOptionCard(_bgUseHdModel, "ISNet General HD (~170 MB)" + (isPro ? "" : "  [PRO]"), vi ? "Tách tóc & viền chi tiết siêu nét" : "Sharp hair & fine-edge segmentation", () => { _bgUseHdModel = true; BuildModeView(); }));

        var modelStatusRow = new DockPanel { Margin = new Thickness(0, 2, 0, 12) };
        if (modelInstalled)
        {
            var delBtn = new Button { Content = vi ? "Xoá model" : "Delete model", Style = S("BfrGhostButton"), Padding = new Thickness(8, 2, 8, 2), FontSize = 11 };
            delBtn.Click += (_, _) =>
            {
                _bgRemover.Dispose();
                _modelManager.DeleteModel(activeModel);
                BuildModeView();
            };
            DockPanel.SetDock(delBtn, Dock.Right);
            modelStatusRow.Children.Add(delBtn);
        }
        modelStatusRow.Children.Add(new TextBlock
        {
            Text = modelInstalled
                ? TryLoc("MediaBgModelReady", "✓ Model đã sẵn sàng trên máy")
                : (vi ? $"Chưa tải ({FormatBytes(activeModel.ApproximateBytes)}) — tự tải khi chạy" : $"Not downloaded ({FormatBytes(activeModel.ApproximateBytes)}) — auto-downloads on run"),
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>(modelInstalled ? "BfrOk" : "BfrFg2"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        leftBody.Children.Add(modelStatusRow);

        // Background fill
        leftBody.Children.Add(new TextBlock { Text = vi ? "NỀN ĐẦU RA" : "OUTPUT BACKGROUND", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg3"), Margin = new Thickness(0, 0, 0, 6) });
        var fillWrap = new WrapPanel();
        fillWrap.Children.Add(MakeFillPill(TryLoc("MediaBgTransparent", "Trong suốt (PNG)"), _bgFillMode == "transparent", () => { _bgFillMode = "transparent"; BuildModeView(); }));
        fillWrap.Children.Add(MakeFillPill(TryLoc("MediaBgWhite", "Nền trắng") + (isPro ? "" : " [PRO]"), _bgFillMode == "white", () => { _bgFillMode = "white"; BuildModeView(); }));
        fillWrap.Children.Add(MakeFillPill(TryLoc("MediaBgBlue", "Nền xanh thẻ") + (isPro ? "" : " [PRO]"), _bgFillMode == "blue", () => { _bgFillMode = "blue"; BuildModeView(); }));
        leftBody.Children.Add(fillWrap);

        leftDock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = leftBody });
        leftCard.Child = leftDock;
        Grid.SetColumn(leftCard, 2);
        grid.Children.Add(leftCard);

        // Left Stage: Side-by-side Before / After preview
        var rightCard = MakeMainCard();
        var previewGrid = new Grid { Margin = new Thickness(16) };
        previewGrid.ColumnDefinitions.Add(new ColumnDefinition());
        previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        previewGrid.ColumnDefinitions.Add(new ColumnDefinition());

        previewGrid.Children.Add(BuildPreviewBox(vi ? "TRƯỚC (ẢNH GỐC)" : "BEFORE (ORIGINAL)", _bgInputImages.FirstOrDefault(), col: 0));
        previewGrid.Children.Add(BuildPreviewBox(vi ? "SAU (ĐÃ TÁCH NỀN)" : "AFTER (BACKGROUND REMOVED)", _bgLastOutputPath, col: 2));

        rightCard.Child = previewGrid;
        Grid.SetColumn(rightCard, 0);
        grid.Children.Add(rightCard);

        return grid;
    }

    private UIElement BuildPreviewBox(string header, string? imagePath, int col)
    {
        var border = new Border
        {
            Background = Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10)
        };
        var dock = new DockPanel { LastChildFill = true };
        var lbl = new TextBlock { Text = header, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg3"), Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(lbl, Dock.Top);
        dock.Children.Add(lbl);

        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                dock.Children.Add(new Image { Source = bmp, Stretch = Stretch.Uniform });
            }
            catch
            {
                dock.Children.Add(new TextBlock { Text = Path.GetFileName(imagePath), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            }
        }
        else
        {
            dock.Children.Add(new TextBlock
            {
                Text = IsVi ? "Chưa có ảnh xem trước" : "No preview yet",
                Foreground = Res<Brush>("BfrFg3"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        border.Child = dock;
        Grid.SetColumn(border, col);
        return border;
    }

    private Button MakeFillPill(string label, bool selected, Action onClick)
    {
        var btn = new Button
        {
            Content = label,
            Style = S(selected ? "BfrPrimaryButton" : "BfrSecondaryButton"),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 6, 6),
            FontSize = 11.5
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private async Task RunRemoveBackgroundAsync()
    {
        if (_isBusy || _bgInputImages.Count == 0) return;
        var vi = IsVi;
        var isPro = _license.Status.IsPro;

        if (!isPro && (_bgInputImages.Count > 1 || _bgUseHdModel || _bgFillMode != "transparent"))
        {
            _requestUpgrade(ProFeature.ImageUnlimited);
            return;
        }

        var modelSpec = _bgUseHdModel ? LocalModelCatalog.RembgIsNetHd : LocalModelCatalog.RembgU2NetP;
        Color? solidBg = _bgFillMode switch
        {
            "white" => Colors.White,
            "blue" => Color.FromRgb(37, 99, 235),
            _ => null
        };

        string? singleOut = null;
        string? batchFolder = null;

        if (_bgInputImages.Count == 1)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "PNG (*.png)|*.png",
                FileName = Path.GetFileNameWithoutExtension(_bgInputImages[0]) + "_nobg.png"
            };
            if (dlg.ShowDialog() != true) return;
            singleOut = dlg.FileName;
        }
        else
        {
            var dlg = new OpenFolderDialog { Title = vi ? "Chọn thư mục lưu ảnh đã tách nền" : "Select output folder" };
            if (dlg.ShowDialog() != true) return;
            batchFolder = dlg.FolderName;
        }

        _alertHost.Children.Clear();
        await ExecuteBusyAsync(vi ? "Đang chuẩn bị model AI & tách nền…" : "Preparing AI model & removing background…", async ct =>
        {
            try
            {
                var dlProgress = new Progress<ModelDownloadProgress>(p =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _processingTitle.Text = vi
                            ? $"Đang tải model {modelSpec.FileName} ({FormatBytes(p.BytesDownloaded)})…"
                            : $"Downloading {modelSpec.FileName} ({FormatBytes(p.BytesDownloaded)})…";
                        _progressBar.IsIndeterminate = false;
                        _progressBar.Value = p.Fraction;
                        _processingPercent.Text = $"{Math.Round(p.Fraction * 100)}%";
                    });
                });

                var modelPath = await _modelManager.EnsureDownloadedAsync(modelSpec, dlProgress, ct);

                var total = _bgInputImages.Count;
                for (var i = 0; i < total; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var input = _bgInputImages[i];
                    var output = singleOut ?? Path.Combine(batchFolder!, Path.GetFileNameWithoutExtension(input) + "_nobg.png");

                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        _processingTitle.Text = vi
                            ? $"Đang tách nền ({i + 1}/{total}): {Path.GetFileName(input)}…"
                            : $"Removing background ({i + 1}/{total}): {Path.GetFileName(input)}…";
                        _progressBar.IsIndeterminate = total == 1;
                        _progressBar.Value = (double)i / total;
                    });

                    var res = await _bgRemover.RemoveBackgroundAsync(input, output, modelPath, solidBg, ct);
                    _bgLastOutputPath = res.OutputPath;
                }

                BuildModeView();
                ShowBanner(
                    vi ? $"✓ Đã tách nền thành công {total} ảnh!" : $"✓ Successfully removed background from {total} image(s)!",
                    isWarning: false);
            }
            catch (OperationCanceledException)
            {
                ShowBanner(vi ? "Đã huỷ tác vụ tách nền." : "Background removal cancelled.", isWarning: true);
            }
            catch (Exception ex)
            {
                ShowBanner((vi ? "Tách nền thất bại: " : "Background removal failed: ") + ex.Message, isWarning: true);
            }
        });
    }

    // =========================================================================
    // TAB 3: OFFLINE AUDIO / VIDEO TRANSCRIBER (Whisper.net + Media Foundation)
    // =========================================================================

    private LocalModelSpec SelectedWhisperModel =>
        _whisperModelId == LocalModelCatalog.WhisperSmall.Id ? LocalModelCatalog.WhisperSmall :
        _whisperModelId == LocalModelCatalog.WhisperBase.Id ? LocalModelCatalog.WhisperBase :
        LocalModelCatalog.WhisperTiny;

    private UIElement BuildTranscribeView()
    {
        var vi = IsVi;
        var isPro = _license.Status.IsPro;
        var activeModel = SelectedWhisperModel;
        var modelInstalled = _modelManager.IsInstalled(activeModel);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 480 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 330 });

        // Left: Controls
        var leftCard = MakeMainCard();
        var leftDock = new DockPanel { LastChildFill = true };

        var runFooter = new Border
        {
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 12, 16, 12)
        };
        var runBtn = new Button
        {
            Content = TryLoc("MediaWhisperRunBtn", "Gỡ băng Audio/Video Offline"),
            Style = S("BfrPrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 9, 16, 9),
            IsEnabled = !_isBusy && !string.IsNullOrWhiteSpace(_whisperInputPath)
        };
        runBtn.Click += async (_, _) => await RunTranscribeAsync();
        runFooter.Child = runBtn;
        DockPanel.SetDock(runFooter, Dock.Bottom);
        leftDock.Children.Add(runFooter);

        var leftBody = new StackPanel { Margin = new Thickness(16) };
        leftBody.Children.Add(new TextBlock { Text = vi ? "Gỡ băng Audio / Video Offline" : "Offline Audio / Video Transcriber", Style = S("BfrHeadingText"), Margin = new Thickness(0, 0, 0, 4) });
        leftBody.Children.Add(new TextBlock
        {
            Text = vi ? "Chạy Whisper.net 100% trên máy — hỗ trợ MP4, MP3, M4A, WAV bằng Windows Media Foundation (không cần FFmpeg)."
                      : "Runs Whisper.net 100% locally — decodes MP4, MP3, M4A, WAV natively via Windows Media Foundation (no FFmpeg).",
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>("BfrFg2"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var pickBtn = new Button
        {
            Content = TryLoc("MediaWhisperSelectFile", "Chọn tệp Audio / Video (hoặc kéo-thả)…"),
            Style = S("BfrSecondaryButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };
        pickBtn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Audio & Video (*.mp4;*.mp3;*.m4a;*.wav;*.wma;*.aac;*.mov;*.mkv)|*.mp4;*.mp3;*.m4a;*.wav;*.wma;*.aac;*.mov;*.mkv|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                _whisperInputPath = dlg.FileName;
                _lastTranscript = null;
                BuildModeView();
            }
        };
        leftBody.Children.Add(pickBtn);

        leftBody.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_whisperInputPath)
                ? (vi ? "Chưa chọn tệp nào." : "No media file selected.")
                : Path.GetFileName(_whisperInputPath),
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>("BfrAccFg"),
            Margin = new Thickness(0, 0, 0, 14),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = _whisperInputPath
        });

        // Whisper Model selector
        leftBody.Children.Add(new TextBlock { Text = vi ? "MÔ HÌNH WHISPER (ON-DEMAND)" : "WHISPER MODEL (ON-DEMAND)", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg3"), Margin = new Thickness(0, 0, 0, 6) });
        leftBody.Children.Add(MakeModeOptionCard(_whisperModelId == LocalModelCatalog.WhisperTiny.Id, "Whisper Tiny (~75 MB)", vi ? "Nhanh nhất — Free thử 60 giây đầu" : "Fastest — Free tries first 60s", () => { _whisperModelId = LocalModelCatalog.WhisperTiny.Id; BuildModeView(); }));
        leftBody.Children.Add(MakeModeOptionCard(_whisperModelId == LocalModelCatalog.WhisperBase.Id, "Whisper Base (~142 MB)" + (isPro ? "" : "  [PRO]"), vi ? "Cân bằng tốc độ & độ chính xác" : "Balanced speed & accuracy", () => { _whisperModelId = LocalModelCatalog.WhisperBase.Id; BuildModeView(); }));
        leftBody.Children.Add(MakeModeOptionCard(_whisperModelId == LocalModelCatalog.WhisperSmall.Id, "Whisper Small HD (~466 MB)" + (isPro ? "" : "  [PRO]"), vi ? "Chuẩn xác cao cho tiếng Việt & họp dài" : "High accuracy for Vietnamese & meetings", () => { _whisperModelId = LocalModelCatalog.WhisperSmall.Id; BuildModeView(); }));

        var statusRow = new DockPanel { Margin = new Thickness(0, 2, 0, 12) };
        if (modelInstalled)
        {
            var delBtn = new Button { Content = vi ? "Xoá model" : "Delete model", Style = S("BfrGhostButton"), Padding = new Thickness(8, 2, 8, 2), FontSize = 11 };
            delBtn.Click += (_, _) => { _modelManager.DeleteModel(activeModel); BuildModeView(); };
            DockPanel.SetDock(delBtn, Dock.Right);
            statusRow.Children.Add(delBtn);
        }
        statusRow.Children.Add(new TextBlock
        {
            Text = modelInstalled
                ? TryLoc("MediaBgModelReady", "✓ Model đã sẵn sàng trên máy")
                : (vi ? $"Chưa tải ({FormatBytes(activeModel.ApproximateBytes)}) — tự tải khi chạy" : $"Not downloaded ({FormatBytes(activeModel.ApproximateBytes)}) — auto-downloads on run"),
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>(modelInstalled ? "BfrOk" : "BfrFg2"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        leftBody.Children.Add(statusRow);

        // Language selector
        leftBody.Children.Add(new TextBlock { Text = TryLoc("MediaWhisperLangLabel", "NGÔN NGỮ ÂM THANH"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg3"), Margin = new Thickness(0, 0, 0, 6) });
        var langCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 10) };
        var langs = new (string Code, string Label)[]
        {
            ("auto", vi ? "Tự động nhận diện" : "Auto-detect"),
            ("vi", "Tiếng Việt (vi)"),
            ("en", "English (en)"),
            ("fr", "Français (fr)"),
            ("zh", "中文 (zh)")
        };
        foreach (var (code, label) in langs)
        {
            var item = new ComboBoxItem { Content = label, Tag = code };
            langCombo.Items.Add(item);
            if (code == _whisperLanguage) langCombo.SelectedItem = item;
        }
        langCombo.SelectionChanged += (_, _) =>
        {
            if (langCombo.SelectedItem is ComboBoxItem { Tag: string code })
                _whisperLanguage = code;
        };
        leftBody.Children.Add(langCombo);

        leftDock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = leftBody });
        leftCard.Child = leftDock;
        Grid.SetColumn(leftCard, 2);
        grid.Children.Add(leftCard);

        // Left Stage: Transcript Preview & Export (.txt / .srt / .vtt)
        var rightCard = MakeMainCard();
        var rightDock = new DockPanel { LastChildFill = true };

        var topBar = new DockPanel { Margin = new Thickness(16, 12, 16, 8) };
        var tabPills = new StackPanel { Orientation = Orientation.Horizontal };
        tabPills.Children.Add(MakeFillPill("TXT", _whisperPreviewTab == "txt", () => { _whisperPreviewTab = "txt"; BuildModeView(); }));
        tabPills.Children.Add(MakeFillPill("SRT", _whisperPreviewTab == "srt", () => { _whisperPreviewTab = "srt"; BuildModeView(); }));
        tabPills.Children.Add(MakeFillPill("VTT", _whisperPreviewTab == "vtt", () => { _whisperPreviewTab = "vtt"; BuildModeView(); }));
        DockPanel.SetDock(tabPills, Dock.Left);
        topBar.Children.Add(tabPills);

        var copyBtn = new Button
        {
            Content = vi ? "Sao chép" : "Copy",
            Style = S("BfrSecondaryButton"),
            Padding = new Thickness(12, 5, 12, 5),
            IsEnabled = _lastTranscript is not null
        };
        copyBtn.Click += (_, _) =>
        {
            var text = GetCurrentTranscriptViewText();
            if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
        };
        DockPanel.SetDock(copyBtn, Dock.Right);
        topBar.Children.Add(copyBtn);
        DockPanel.SetDock(topBar, Dock.Top);
        rightDock.Children.Add(topBar);

        var exportBar = new Border
        {
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Res<Brush>("BfrGlassFillStrong"),
            CornerRadius = new CornerRadius(0, 0, 18, 18),
            Padding = new Thickness(16, 10, 16, 10)
        };
        var exportRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnTxt = new Button { Content = TryLoc("MediaWhisperExportTxt", "Lưu .txt"), Style = S("BfrSecondaryButton"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), IsEnabled = _lastTranscript is not null };
        btnTxt.Click += (_, _) => ExportTranscriptFile("txt", requirePro: false);
        var btnSrt = new Button { Content = TryLoc("MediaWhisperExportSrt", "Lưu phụ đề .srt") + (isPro ? "" : " [PRO]"), Style = S("BfrPrimaryButton"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), IsEnabled = _lastTranscript is not null };
        btnSrt.Click += (_, _) => ExportTranscriptFile("srt", requirePro: true);
        var btnVtt = new Button { Content = TryLoc("MediaWhisperExportVtt", "Lưu .vtt") + (isPro ? "" : " [PRO]"), Style = S("BfrSecondaryButton"), Padding = new Thickness(12, 6, 12, 6), IsEnabled = _lastTranscript is not null };
        btnVtt.Click += (_, _) => ExportTranscriptFile("vtt", requirePro: true);
        exportRow.Children.Add(btnTxt);
        exportRow.Children.Add(btnSrt);
        exportRow.Children.Add(btnVtt);
        exportBar.Child = exportRow;
        DockPanel.SetDock(exportBar, Dock.Bottom);
        rightDock.Children.Add(exportBar);

        var transcriptBox = new TextBox
        {
            Text = GetCurrentTranscriptViewText(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12.5,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(16, 0, 16, 12),
            Style = S("BfrTextField")
        };
        rightDock.Children.Add(transcriptBox);

        rightCard.Child = rightDock;
        Grid.SetColumn(rightCard, 0);
        grid.Children.Add(rightCard);

        return grid;
    }

    private string GetCurrentTranscriptViewText()
    {
        if (_lastTranscript is null)
            return IsVi ? "Chọn tệp Audio/Video bên trái rồi bấm 'Gỡ băng Audio/Video Offline' để bắt đầu." : "Select an Audio/Video file on the left and click Transcribe to begin.";

        return _whisperPreviewTab switch
        {
            "srt" => _lastTranscript.SrtText,
            "vtt" => _lastTranscript.VttText,
            _ => _lastTranscript.PlainText
        };
    }

    private void ExportTranscriptFile(string ext, bool requirePro)
    {
        if (_lastTranscript is null) return;
        if (requirePro && !_license.Status.IsPro)
        {
            _requestUpgrade(ProFeature.OfflineOcr);
            return;
        }

        var content = ext switch
        {
            "srt" => _lastTranscript.SrtText,
            "vtt" => _lastTranscript.VttText,
            _ => _lastTranscript.PlainText
        };

        var dlg = new SaveFileDialog
        {
            Filter = $"{ext.ToUpperInvariant()} (*.{ext})|*.{ext}",
            FileName = Path.GetFileNameWithoutExtension(_lastTranscript.InputPath) + "." + ext
        };
        if (dlg.ShowDialog() != true) return;

        File.WriteAllText(dlg.FileName, content, new System.Text.UTF8Encoding(false));
        ShowBanner(
            IsVi ? $"✓ Đã lưu tệp {Path.GetFileName(dlg.FileName)}" : $"✓ Saved {Path.GetFileName(dlg.FileName)}",
            isWarning: false);
    }

    private async Task RunTranscribeAsync()
    {
        if (_isBusy || string.IsNullOrWhiteSpace(_whisperInputPath)) return;
        var vi = IsVi;
        var isPro = _license.Status.IsPro;
        var modelSpec = SelectedWhisperModel;

        if (!isPro && modelSpec.RequiresPro)
        {
            _requestUpgrade(ProFeature.OfflineOcr);
            return;
        }

        _alertHost.Children.Clear();
        await ExecuteBusyAsync(vi ? "Đang chuẩn bị model Whisper & gỡ băng…" : "Preparing Whisper model & transcribing…", async ct =>
        {
            try
            {
                var dlProgress = new Progress<ModelDownloadProgress>(p =>
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        _processingTitle.Text = vi
                            ? $"Đang tải model {modelSpec.FileName} ({FormatBytes(p.BytesDownloaded)})…"
                            : $"Downloading {modelSpec.FileName} ({FormatBytes(p.BytesDownloaded)})…";
                        _progressBar.IsIndeterminate = false;
                        _progressBar.Value = p.Fraction;
                        _processingPercent.Text = $"{Math.Round(p.Fraction * 100)}%";
                    });
                });

                var modelPath = await _modelManager.EnsureDownloadedAsync(modelSpec, dlProgress, ct);

                var trProgress = new Progress<double>(p =>
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        _processingTitle.Text = vi ? "Đang gỡ băng âm thanh offline (Whisper)…" : "Transcribing audio offline (Whisper)…";
                        _progressBar.IsIndeterminate = false;
                        _progressBar.Value = Math.Clamp(p, 0, 1);
                        _processingPercent.Text = $"{Math.Round(p * 100)}%";
                    });
                });

                TimeSpan? maxDur = isPro ? null : OfflineAudioTranscriber.FreeTierMaxDuration;
                _lastTranscript = await _transcriber.TranscribeAsync(_whisperInputPath, modelPath, _whisperLanguage, maxDur, trProgress, ct);

                BuildModeView();
                if (_lastTranscript.WasTruncatedByFreeLimit)
                {
                    ShowBanner(
                        vi ? "Bản Free đã gỡ băng thử 60 giây đầu tiên. Nâng cấp Pro để gỡ băng không giới hạn thời lượng và xuất phụ đề .srt/.vtt."
                           : "Free tier transcribed the first 60 seconds. Upgrade to Pro for unlimited duration and .srt/.vtt subtitle export.",
                        isWarning: true);
                }
                else
                {
                    ShowBanner(
                        vi ? $"✓ Đã gỡ băng xong ({_lastTranscript.Segments.Count} đoạn · {_lastTranscript.ProcessedDuration:mm\\:ss})."
                           : $"✓ Transcription complete ({_lastTranscript.Segments.Count} segments · {_lastTranscript.ProcessedDuration:mm\\:ss}).",
                        isWarning: false);
                }
            }
            catch (OperationCanceledException)
            {
                ShowBanner(vi ? "Đã huỷ tác vụ gỡ băng." : "Transcription cancelled.", isWarning: true);
            }
            catch (Exception ex)
            {
                ShowBanner((vi ? "Gỡ băng thất bại: " : "Transcription failed: ") + ex.Message, isWarning: true);
            }
        });
    }

    // =========================================================================
    // TAB 1: DUPLICATE & SIMILAR IMAGE FINDER
    // =========================================================================

    private UIElement BuildDuplicatesView()
    {
        var vi = IsVi;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 480 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 330 });

        // Left: Controls Inspector
        var leftCard = MakeMainCard();
        var leftDock = new DockPanel { LastChildFill = true };

        var scanFooter = new Border
        {
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 12, 16, 12)
        };
        var scanBtn = new Button
        {
            Content = TryLoc("MediaDupScanBtn", "Quét tìm tệp trùng"),
            Style = S("BfrPrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 9, 16, 9),
            IsEnabled = !_isBusy && !string.IsNullOrWhiteSpace(_scanFolder)
        };
        scanBtn.Click += async (_, _) => await RunScanAsync();
        scanFooter.Child = scanBtn;
        DockPanel.SetDock(scanFooter, Dock.Bottom);
        leftDock.Children.Add(scanFooter);

        var leftBody = new StackPanel { Margin = new Thickness(16) };
        leftBody.Children.Add(new TextBlock
        {
            Text = vi ? "Quét file trùng & Ảnh giống nhau" : "Duplicate & Similar Finder",
            Style = S("BfrHeadingText"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        leftBody.Children.Add(new TextBlock
        {
            Text = vi ? "Thuật toán 3 tầng siêu tốc (Kích thước → 64KB đầu/cuối → SHA-256) và Perceptual dHash cho ảnh."
                      : "Fast 3-tier engine (Size → 64KB Head/Tail → SHA-256) plus Perceptual dHash for similar images.",
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>("BfrFg2"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        // Folder picker
        leftBody.Children.Add(new TextBlock { Text = vi ? "THƯ MỤC CẦN QUÉT (hoặc kéo-thả vào đây)" : "FOLDER TO SCAN (or drag & drop)", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg3"), Margin = new Thickness(0, 0, 0, 6) });
        var folderDock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var browseBtn = new Button { Content = vi ? "Chọn…" : "Browse…", Style = S("BfrSecondaryButton"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = vi ? "Chọn thư mục cần quét trùng lặp" : "Select folder to scan" };
            if (dlg.ShowDialog() == true)
            {
                _scanFolder = dlg.FolderName;
                BuildModeView();
            }
        };
        DockPanel.SetDock(browseBtn, Dock.Right);
        folderDock.Children.Add(browseBtn);
        folderDock.Children.Add(new TextBox
        {
            Text = _scanFolder,
            IsReadOnly = true,
            Style = S("BfrTextField"),
            ToolTip = _scanFolder
        });
        leftBody.Children.Add(folderDock);

        var subCb = new CheckBox
        {
            Content = vi ? "Quét cả thư mục con (đệ quy)" : "Include subfolders",
            IsChecked = _includeSubfolders,
            Foreground = Res<Brush>("BfrFg"),
            Margin = new Thickness(0, 0, 0, 14)
        };
        subCb.Checked += (_, _) => _includeSubfolders = true;
        subCb.Unchecked += (_, _) => _includeSubfolders = false;
        leftBody.Children.Add(subCb);

        // Scan mode: Exact SHA-256 vs Similar Images (dHash)
        leftBody.Children.Add(new TextBlock { Text = vi ? "CHẾ ĐỘ QUÉT" : "SCAN MODE", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("BfrFg3"), Margin = new Thickness(0, 0, 0, 6) });
        leftBody.Children.Add(MakeModeOptionCard(!_similarImagesMode, TryLoc("MediaDupExactMode", "Trùng nội dung tuyệt đối (SHA-256)"), vi ? "Chính xác 100%, không bao giờ nhầm lẫn" : "100% byte-identical, zero false positives", () => { _similarImagesMode = false; BuildModeView(); }));
        leftBody.Children.Add(MakeModeOptionCard(_similarImagesMode, TryLoc("MediaDupSimilarMode", "Ảnh gần giống (Perceptual dHash)") + (_license.Status.IsPro ? "" : "  [PRO]"), vi ? "Tìm ảnh chụp liên tiếp, khác độ phân giải hoặc bị nén lại" : "Finds resized, re-compressed, or burst-shot photos", () => { _similarImagesMode = true; BuildModeView(); }));

        if (_similarImagesMode)
        {
            leftBody.Children.Add(new TextBlock
            {
                Text = vi ? $"Độ nhạy giống nhau (Hamming ≤ {_similarMaxDistance} bit):" : $"Similarity threshold (Hamming ≤ {_similarMaxDistance} bits):",
                Style = S("BfrCaptionText"),
                Margin = new Thickness(0, 8, 0, 4)
            });
            var slider = new Slider { Minimum = 2, Maximum = 12, Value = _similarMaxDistance, IsSnapToTickEnabled = true, TickFrequency = 1 };
            slider.ValueChanged += (_, e) => _similarMaxDistance = (int)e.NewValue;
            leftBody.Children.Add(slider);
        }

        if (!_license.Status.IsPro)
        {
            leftBody.Children.Add(new Border
            {
                Background = Res<Brush>("BfrWarnSoft"),
                BorderBrush = Res<Brush>("BfrWarn"),
                BorderThickness = new Thickness(3, 1, 1, 1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 12, 0, 0),
                Child = new TextBlock
                {
                    Text = vi
                        ? $"Bản Free quét tối đa {MediaCleanStudioViewModel.FreePreviewFileCap} tệp/lần. Nâng cấp Pro để quét không giới hạn, tìm ảnh tương tự và tự động chọn bản giữ lại."
                        : $"Free tier scans up to {MediaCleanStudioViewModel.FreePreviewFileCap} files/run. Upgrade to Pro for unlimited scans, similar image matching, and smart auto-select.",
                    Style = S("BfrCaptionText"),
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        leftDock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = leftBody });
        leftCard.Child = leftDock;
        Grid.SetColumn(leftCard, 2);
        grid.Children.Add(leftCard);

        // Left Stage: Results + Smart Auto-Select + Safe Recycle Bin Footer
        var rightCard = MakeMainCard();
        var rightDock = new DockPanel { LastChildFill = true };

        // Top action bar
        var topBar = new DockPanel
        {
            Margin = new Thickness(16, 12, 16, 10),
            LastChildFill = true
        };
        var autoStack = new StackPanel { Orientation = Orientation.Horizontal };
        autoStack.Children.Add(MakeSmallActionBtn(vi ? "Giữ mới nhất" : "Keep Newest", () => ApplyAutoRule(DuplicateKeepRule.KeepNewest)));
        autoStack.Children.Add(MakeSmallActionBtn(vi ? "Giữ lớn nhất" : "Keep Largest", () => ApplyAutoRule(DuplicateKeepRule.KeepLargest)));
        autoStack.Children.Add(MakeSmallActionBtn(vi ? "Bỏ chọn" : "Clear", () => { _vm.ClearSelections(); RenderGroups(); }));
        DockPanel.SetDock(autoStack, Dock.Right);
        topBar.Children.Add(autoStack);

        _summaryStatsText = new TextBlock
        {
            Style = S("BfrBodyText"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        topBar.Children.Add(_summaryStatsText);
        DockPanel.SetDock(topBar, Dock.Top);
        rightDock.Children.Add(topBar);

        // Bottom Recycle Bin bar
        var bottomBar = new Border
        {
            BorderBrush = Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Res<Brush>("BfrGlassFillStrong"),
            CornerRadius = new CornerRadius(0, 0, 18, 18),
            Padding = new Thickness(16, 10, 16, 10)
        };
        var bottomDock = new DockPanel();
        _recycleBtn = new Button
        {
            Content = TryLoc("MediaDupRecycleBtn", "Đưa mục đã chọn vào Thùng rác"),
            Style = S("BfrPrimaryButton"),
            Padding = new Thickness(16, 8, 16, 8)
        };
        _recycleBtn.Click += async (_, _) => await RunRecycleSelectedAsync();
        DockPanel.SetDock(_recycleBtn, Dock.Right);
        bottomDock.Children.Add(_recycleBtn);

        var renameBtn = new Button
        {
            Content = vi ? "Đẩy sang Đổi tên" : "Send to Renamer",
            Style = S("BfrSecondaryButton"),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        renameBtn.Click += (_, _) =>
        {
            var allPaths = _vm.DuplicateGroups.SelectMany(g => g.Files.Select(f => f.FilePath)).Distinct().ToList();
            if (allPaths.Count > 0) _sendToRename(allPaths);
        };
        DockPanel.SetDock(renameBtn, Dock.Right);
        bottomDock.Children.Add(renameBtn);

        bottomDock.Children.Add(new TextBlock
        {
            Text = vi ? "An toàn: luôn chuyển vào Thùng rác Windows, không xoá vĩnh viễn." : "Safe cleanup: always moves to Windows Recycle Bin.",
            Style = S("BfrCaptionText"),
            Foreground = Res<Brush>("BfrFg2"),
            VerticalAlignment = VerticalAlignment.Center
        });
        bottomBar.Child = bottomDock;
        DockPanel.SetDock(bottomBar, Dock.Bottom);
        rightDock.Children.Add(bottomBar);

        _groupsHost = new StackPanel { Margin = new Thickness(16, 4, 16, 16) };
        rightDock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _groupsHost });

        rightCard.Child = rightDock;
        Grid.SetColumn(rightCard, 0);
        grid.Children.Add(rightCard);

        RenderGroups();
        return grid;
    }

    private void RenderGroups()
    {
        if (_groupsHost is null || _summaryStatsText is null || _recycleBtn is null) return;
        _groupsHost.Children.Clear();
        var vi = IsVi;

        var selCount = _vm.SelectedForDeleteCount;
        var selBytes = _vm.SelectedForDeleteBytes;
        _recycleBtn.IsEnabled = !_isBusy && selCount > 0;
        _recycleBtn.Content = selCount > 0
            ? (vi ? $"Đưa {selCount} tệp vào Thùng rác ({FormatBytes(selBytes)})" : $"Move {selCount} files to Recycle Bin ({FormatBytes(selBytes)})")
            : TryLoc("MediaDupRecycleBtn", "Đưa mục đã chọn vào Thùng rác");

        if (_vm.DuplicateGroups.Count == 0)
        {
            _summaryStatsText.Text = _vm.ScannedFileCount > 0
                ? (vi ? $"✓ Không có tệp trùng nào ({_vm.ScannedFileCount:N0} tệp đã quét)" : $"✓ No duplicates found ({_vm.ScannedFileCount:N0} scanned)")
                : (vi ? "Chọn thư mục bên trái để bắt đầu quét" : "Select a folder on the left to start scanning");
            return;
        }

        _summaryStatsText.Text = vi
            ? $"{_vm.DuplicateGroups.Count} nhóm trùng · Có thể tiết kiệm {FormatBytes(_vm.TotalWastedBytes)}"
            : $"{_vm.DuplicateGroups.Count} groups · Potential savings {FormatBytes(_vm.TotalWastedBytes)}";

        foreach (var group in _vm.DuplicateGroups.Take(100))
        {
            var card = new Border
            {
                Background = Res<Brush>("BfrGlassFillStrong"),
                BorderBrush = Res<Brush>("BfrGlassRim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = group.IsSimilarGroup
                    ? (vi ? $"Nhóm ảnh giống #{group.GroupIndex} · {group.Files.Count} ảnh" : $"Similar Group #{group.GroupIndex} · {group.Files.Count} images")
                    : (vi ? $"Nhóm trùng #{group.GroupIndex} · {group.Files.Count} bản · {FormatBytes(group.SizeBytes)}/tệp" : $"Duplicate Group #{group.GroupIndex} · {group.Files.Count} copies · {FormatBytes(group.SizeBytes)} each"),
                FontWeight = FontWeights.SemiBold,
                Foreground = Res<Brush>("BfrFg"),
                Margin = new Thickness(0, 0, 0, 8)
            });

            foreach (var entry in group.Files)
            {
                var captured = entry;
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };

                var cb = new CheckBox
                {
                    IsChecked = captured.IsSelectedForDelete,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                cb.Checked += (_, _) => { captured.IsSelectedForDelete = true; UpdateSelectionFooter(); };
                cb.Unchecked += (_, _) => { captured.IsSelectedForDelete = false; UpdateSelectionFooter(); };
                DockPanel.SetDock(cb, Dock.Left);
                row.Children.Add(cb);

                if (captured.IsImage)
                {
                    var thumb = TryLoadSmallThumb(captured.FilePath);
                    if (thumb is not null)
                    {
                        var img = new Image { Source = thumb, Width = 38, Height = 38, Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 10, 0) };
                        DockPanel.SetDock(img, Dock.Left);
                        row.Children.Add(img);
                    }
                }

                var metaRight = new TextBlock
                {
                    Text = $"{FormatBytes(captured.SizeBytes)} · {captured.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm}",
                    Style = S("BfrCaptionText"),
                    Foreground = Res<Brush>("BfrFg3"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                DockPanel.SetDock(metaRight, Dock.Right);
                row.Children.Add(metaRight);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock { Text = captured.FileName, Foreground = Res<Brush>("BfrFg"), FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
                info.Children.Add(new TextBlock { Text = captured.DirectoryPath, Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg2"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = captured.FilePath });
                row.Children.Add(info);

                sp.Children.Add(row);
            }

            card.Child = sp;
            _groupsHost.Children.Add(card);
        }
    }

    private void UpdateSelectionFooter()
    {
        if (_recycleBtn is null) return;
        var vi = IsVi;
        var selCount = _vm.SelectedForDeleteCount;
        var selBytes = _vm.SelectedForDeleteBytes;
        _recycleBtn.IsEnabled = !_isBusy && selCount > 0;
        _recycleBtn.Content = selCount > 0
            ? (vi ? $"Đưa {selCount} tệp vào Thùng rác ({FormatBytes(selBytes)})" : $"Move {selCount} files to Recycle Bin ({FormatBytes(selBytes)})")
            : TryLoc("MediaDupRecycleBtn", "Đưa mục đã chọn vào Thùng rác");
    }

    private void ApplyAutoRule(DuplicateKeepRule rule)
    {
        if (!_vm.ApplyAutoSelectRule(rule))
        {
            if (_vm.UpgradeRequiredFeature is { } feat) _requestUpgrade(feat);
            return;
        }
        RenderGroups();
    }

    private async Task RunScanAsync()
    {
        if (string.IsNullOrWhiteSpace(_scanFolder) || _isBusy) return;
        _alertHost.Children.Clear();
        await ExecuteBusyAsync(IsVi ? "Đang quét tìm tệp trùng lặp…" : "Scanning for duplicates…", async ct =>
        {
            var ok = await _vm.ScanDuplicatesAsync(_scanFolder, _includeSubfolders, _similarImagesMode, _similarMaxDistance, ct);
            if (!ok && _vm.UpgradeRequiredFeature is { } feat)
            {
                _requestUpgrade(feat);
                return;
            }
            RenderGroups();
            if (_vm.WasTruncatedByFreeLimit)
            {
                ShowBanner(
                    IsVi ? $"Bản Free đã dừng ở {MediaCleanStudioViewModel.FreePreviewFileCap} tệp đầu tiên. Nâng cấp Pro để quét toàn bộ thư mục."
                         : $"Free tier stopped at {MediaCleanStudioViewModel.FreePreviewFileCap} files. Upgrade to Pro for unlimited scans.",
                    isWarning: true);
            }
        });
    }

    private async Task RunRecycleSelectedAsync()
    {
        if (_isBusy || _vm.SelectedForDeleteCount == 0) return;
        _alertHost.Children.Clear();
        await ExecuteBusyAsync(IsVi ? "Đang chuyển tệp trùng vào Thùng rác…" : "Moving duplicates to Recycle Bin…", async ct =>
        {
            var (deleted, reclaimed, failed) = await _vm.RecycleSelectedAsync(MoveFileToRecycleBin, ct);
            RenderGroups();
            ShowBanner(
                IsVi ? $"Đã chuyển {deleted} tệp vào Thùng rác, giải phóng {FormatBytes(reclaimed)}" + (failed > 0 ? $" ({failed} lỗi)" : ".")
                     : $"Moved {deleted} file(s) to Recycle Bin, freed {FormatBytes(reclaimed)}" + (failed > 0 ? $" ({failed} failed)" : "."),
                isWarning: failed > 0);
        });
    }

    private async Task ExecuteBusyAsync(string label, Func<CancellationToken, Task> work)
    {
        _isBusy = true;
        _opCts?.Cancel();
        _opCts?.Dispose();
        _opCts = new CancellationTokenSource();

        _processingTitle.Text = label;
        _processingPercent.Text = "";
        _progressBar.IsIndeterminate = true;
        _progressBar.Value = 0;
        _processingCard.Visibility = Visibility.Visible;

        try
        {
            await work(_opCts.Token);
        }
        finally
        {
            _isBusy = false;
            _processingCard.Visibility = Visibility.Collapsed;
            UpdateSelectionFooter();
        }
    }

    private void ShowBanner(string message, bool isWarning)
    {
        _alertHost.Children.Clear();
        var card = new Border
        {
            Background = Res<Brush>(isWarning ? "BfrWarnSoft" : "BfrOkSoft"),
            BorderBrush = Res<Brush>(isWarning ? "BfrWarn" : "BfrOk"),
            BorderThickness = new Thickness(3, 1, 1, 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock { Text = message, Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg"), TextWrapping = TextWrapping.Wrap }
        };
        _alertHost.Children.Add(card);
    }

    private Border MakeMainCard() => new()
    {
        Background = Res<Brush>("BfrGlassFill"),
        BorderBrush = Res<Brush>("BfrGlassRim"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(22)
    };

    private UIElement MakeModeOptionCard(bool selected, string title, string desc, Action onClick)
    {
        var border = new Border
        {
            Background = selected ? Res<Brush>("BfrAccGradientSoft") : Res<Brush>("BfrGlassFillStrong"),
            BorderBrush = selected ? Res<Brush>("BfrAccFg") : Res<Brush>("BfrGlassRim"),
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = Cursors.Hand
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = Res<Brush>("BfrFg") });
        sp.Children.Add(new TextBlock { Text = desc, Style = S("BfrCaptionText"), Foreground = Res<Brush>("BfrFg2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        border.Child = sp;
        border.MouseLeftButtonUp += (_, _) => onClick();
        return border;
    }

    private Button MakeSmallActionBtn(string text, Action onClick)
    {
        var btn = new Button
        {
            Content = text,
            Style = S("BfrSecondaryButton"),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 11.5
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static BitmapImage? TryLoadSmallThumb(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 64;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // Safe Windows Shell Recycle Bin P/Invoke (zero extra dependencies)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

    private static bool MoveFileToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 0x0003, // FO_DELETE
            pFrom = path + "\0\0",
            fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400 // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.##} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes} B";
    }

    private static Style S(string key) => (Style)Application.Current.FindResource(key);
    private static T Res<T>(string key) where T : class => (T)Application.Current.FindResource(key);
    private static string TryLoc(string key, string fallback)
    {
        var val = Application.Current?.TryFindResource(key) as string;
        return string.IsNullOrEmpty(val) ? fallback : val;
    }
}
