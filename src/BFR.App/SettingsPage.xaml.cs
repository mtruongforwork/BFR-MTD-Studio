namespace BFR.App;

using BFR.Core.Licensing;
using BFR.App.Infrastructure;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Ui = Wpf.Ui.Controls;

/// <summary>
/// Trang Cài đặt (v1.4: một trang trong khung điều hướng): ngôn ngữ, giao diện, Shelf/chạy nền,
/// phím tắt, cập nhật, giấy phép Pro, quyền riêng tư. Mỗi nhóm là một Card của WPF-UI,
/// tuỳ chọn bật/tắt dùng ToggleSwitch.
/// </summary>
public class SettingsPage : PageBase
{
    /// <summary>Người dùng bấm "Nâng cấp" trong nhóm giấy phép.</summary>
    public event EventHandler? UpgradeRequested;

    /// <summary>Trang bị rời đi: cửa sổ chính áp dụng các thay đổi (khay, hook, phím tắt...).</summary>
    public event EventHandler? SettingsLeft;

    /// <summary>Một cài đặt vừa đổi: cửa sổ chính áp dụng ngay (phím tắt, lắc chuột, chạy nền) thay vì đợi rời trang.</summary>
    public event EventHandler? SettingsChanged;

    public override void OnHidden() => SettingsLeft?.Invoke(this, EventArgs.Empty);

    private readonly LicenseCoordinator _license;
    private readonly AppSettings _settings;

    public SettingsPage(LicenseCoordinator license)
    {
        _license = license;
        _settings = SettingsStore.Load();

        AutomationProperties.SetName(this, "Settings window");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new TextBlock
        {
            Text = Loc.T("SettingsTitle"),
            FontSize = (double)Application.Current.FindResource("AppFontSizeTitle"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
            Margin = new Thickness(28, 20, 28, 6)
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var content = new TileColumns();

        // ---- Giao diện & ngôn ngữ ----
        var look = Card(content, Loc.T("SettingsLanguage") + " · " + Loc.T("SettingsTheme"));

        var language = new ComboBox { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(language, "Language selector");
        foreach (var code in new[] { "vi", "en", "fr", "zh-Hans" })
        {
            language.Items.Add(new ComboBoxItem { Content = LanguageName(code), Tag = code });
        }
        SelectByTag(language, _settings.Language);
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedItem is ComboBoxItem { Tag: string code })
            {
                Loc.Apply(code);
                _settings.Language = code;
                SettingsStore.Save(_settings);
            }
        };
        look.Children.Add(Caption(Loc.T("SettingsLanguage")));
        look.Children.Add(language);

        var theme = new ComboBox { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(theme, "Theme selector");
        theme.Items.Add(new ComboBoxItem { Content = Loc.T("ThemeSystem"), Tag = "System" });
        theme.Items.Add(new ComboBoxItem { Content = Loc.T("ThemeLight"), Tag = "Light" });
        theme.Items.Add(new ComboBoxItem { Content = Loc.T("ThemeDark"), Tag = "Dark" });
        SelectByTag(theme, _settings.Theme);
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is ComboBoxItem { Tag: string tag }
                && Enum.TryParse<ThemeManager.AppTheme>(tag, out var value))
            {
                _settings.Theme = tag;
                SettingsStore.Save(_settings);
                ThemeManager.Apply(value);
            }
        };
        var themeCaption = Caption(Loc.T("SettingsTheme"));
        themeCaption.Margin = new Thickness(0, 12, 0, 4);
        look.Children.Add(themeCaption);
        look.Children.Add(theme);

        // Bảng màu nhấn: áp dụng ngay, giữ theme sáng/tối hiện tại, kèm chấm màu minh họa.
        var accentCaption = Caption(Loc.T("SettingsAccent"));
        accentCaption.Margin = new Thickness(0, 12, 0, 4);
        look.Children.Add(accentCaption);
        var accentTrack = new Border { Style = (Style)Application.Current.FindResource("BfrSegmentedTrack"), HorizontalAlignment = HorizontalAlignment.Left };
        var accentRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (name, c1, c2) in new[]
        {
            ("Aurora", Color.FromRgb(0x63, 0x66, 0xF1), Color.FromRgb(0xA8, 0x55, 0xF7)),
            ("Mint", Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x14, 0xB8, 0xA6)),
            ("Sunset", Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xEC, 0x48, 0x99))
        })
        {
            var swatch = new System.Windows.Shapes.Ellipse
            {
                Width = 10,
                Height = 10,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new LinearGradientBrush(c1, c2, 45)
            };
            var labelRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            labelRow.Children.Add(swatch);
            labelRow.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });

            var option = new RadioButton
            {
                Content = labelRow,
                GroupName = "accent",
                Style = (Style)Application.Current.FindResource("BfrSegmentedItem"),
                IsChecked = string.Equals(_settings.AccentPalette, name, StringComparison.OrdinalIgnoreCase)
            };
            var chosen = name;
            option.Checked += (_, _) =>
            {
                if (string.Equals(_settings.AccentPalette, chosen, StringComparison.OrdinalIgnoreCase)) return;
                _settings.AccentPalette = chosen;
                SettingsStore.Save(_settings);
                ThemeManager.Apply(ThemeManager.Current);
            };
            accentRow.Children.Add(option);
        }
        accentTrack.Child = accentRow;
        look.Children.Add(accentTrack);

        var mascot = Toggle(Loc.T("SettingsMascot"), _settings.ShowMascot, on =>
        {
            _settings.ShowMascot = on;
            SettingsStore.Save(_settings);
            SettingsStore.NotifyMascotVisibilityChanged(on);
        });
        mascot.Margin = new Thickness(0, 14, 0, 0);
        look.Children.Add(mascot);

        // Giảm hiệu ứng (quyết định 6.2): áp dụng ngay, không cần khởi động lại.
        var effects = Toggle(Loc.T("SettingsReduceEffects"), _settings.ReduceEffects, on =>
        {
            _settings.ReduceEffects = on;
            SettingsStore.Save(_settings);
            ThemeManager.Apply(ThemeManager.Current);
        });
        effects.Margin = new Thickness(0, 12, 0, 0);
        AutomationProperties.SetName(effects, "Reduce visual effects");
        look.Children.Add(effects);
        look.Children.Add(Caption(BfrTheme.NoHardwareAcceleration ? Loc.T("SettingsReduceEffectsAuto") : Loc.T("SettingsReduceEffectsHint")));

        // ---- Shelf & chạy nền ----
        var shelf = Card(content, Loc.T("SettingsShelf"));
        shelf.Children.Add(Toggle(
            Loc.T("SettingsShake"),
            _settings.ShakeToOpenShelf,
            on => _settings.ShakeToOpenShelf = on));
        shelf.Children.Add(Caption(Loc.T("SettingsShakeHint")));
        var hotkeyToggle = Toggle(Loc.T("SettingsHotkey"), _settings.ShelfHotkeyEnabled, on => _settings.ShelfHotkeyEnabled = on);
        hotkeyToggle.Margin = new Thickness(0, 12, 0, 0);
        shelf.Children.Add(hotkeyToggle);

        var hotkeyBox = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(hotkeyBox, "Shelf hotkey");
        foreach (var h in BFR.Core.HotkeySpec.Presets) hotkeyBox.Items.Add(h);
        hotkeyBox.SelectedItem = BFR.Core.HotkeySpec.TryParse(_settings.ShelfHotkey, out var currentHotkey)
            ? currentHotkey.ToString()
            : BFR.Core.HotkeySpec.Default;
        hotkeyBox.SelectionChanged += (_, _) =>
        {
            if (hotkeyBox.SelectedItem is string chosen) { _settings.ShelfHotkey = chosen; SettingsStore.Save(_settings); SettingsChanged?.Invoke(this, EventArgs.Empty); }
        };
        shelf.Children.Add(hotkeyBox);
        shelf.Children.Add(Caption(Loc.T("SettingsHotkeyHint")));

        // Menu tròn các công cụ: phím tắt toàn cục riêng, mặc định tắt.
        var radialToggle = Toggle(Loc.T("SettingsRadial"), _settings.RadialHotkeyEnabled, on => _settings.RadialHotkeyEnabled = on);
        radialToggle.Margin = new Thickness(0, 12, 0, 0);
        shelf.Children.Add(radialToggle);
        var radialBox = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(radialBox, "Radial menu hotkey");
        foreach (var h in BFR.Core.HotkeySpec.Presets) radialBox.Items.Add(h);
        radialBox.SelectedItem = BFR.Core.HotkeySpec.TryParse(_settings.RadialHotkey, out var currentRadial)
            ? currentRadial.ToString()
            : "Ctrl+Alt+Q";
        radialBox.SelectionChanged += (_, _) =>
        {
            if (radialBox.SelectedItem is string chosen) { _settings.RadialHotkey = chosen; SettingsStore.Save(_settings); SettingsChanged?.Invoke(this, EventArgs.Empty); }
        };
        shelf.Children.Add(radialBox);
        shelf.Children.Add(Caption(Loc.T("SettingsRadialHint")));

        // Giữ chuột giữa mở Menu Tròn ngữ cảnh (v3.0).
        var mmbToggle = Toggle(Loc.T("SettingsMiddleMouseRadial"), _settings.MiddleMouseRadialEnabled, on => _settings.MiddleMouseRadialEnabled = on);
        mmbToggle.Margin = new Thickness(0, 12, 0, 0);
        shelf.Children.Add(mmbToggle);

        var mmbHoldBox = new ComboBox { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(mmbHoldBox, "Middle mouse hold duration");
        foreach (var ms in new[] { 150, 220, 350, 500 })
            mmbHoldBox.Items.Add(new ComboBoxItem { Content = $"{ms} ms", Tag = ms.ToString() });
        SelectByTag(mmbHoldBox, AppSettings.NormalizeMiddleMouseHoldMs(_settings.MiddleMouseHoldMs).ToString());
        mmbHoldBox.SelectionChanged += (_, _) =>
        {
            if (mmbHoldBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var ms))
            {
                _settings.MiddleMouseHoldMs = AppSettings.NormalizeMiddleMouseHoldMs(ms);
                SettingsStore.Save(_settings);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        shelf.Children.Add(mmbHoldBox);
        shelf.Children.Add(Caption(Loc.T("SettingsMiddleMouseRadialHint")));

        var bg = Toggle(
            Loc.T("SettingsRunInBackground"),
            _settings.RunInBackground,
            on => _settings.RunInBackground = on);
        bg.Margin = new Thickness(0, 12, 0, 0);
        shelf.Children.Add(bg);

        // Khởi động cùng Windows: chỉ có nghĩa khi bật chạy nền (ứng dụng khởi động ẩn trong khay).
        Ui.ToggleSwitch? startup = null;
        var startupHint = Caption(Loc.T("SettingsStartupBlocked"));
        startupHint.Visibility = Visibility.Collapsed;
        var openStartupApps = new Ui.Button
        {
            Content = Loc.T("SettingsOpenStartupApps"),
            Appearance = Ui.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed
        };
        openStartupApps.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); }
            catch { /* không mở được trang cài đặt: bỏ qua */ }
        };

        var suppress = false;   // đặt IsChecked bằng code không được kích hoạt lại logic bật/tắt
        startup = Toggle(Loc.T("SettingsStartWithWindows"), false, _ => { });
        startup.Margin = new Thickness(0, 12, 0, 0);
        startup.Checked += async (_, _) =>
        {
            if (suppress) return;
            // Khởi động ẩn chỉ hợp lý khi có biểu tượng khay: tự bật chạy nền.
            if (bg.IsChecked != true) bg.IsChecked = true;
            var state = await AutoStart.SetEnabledAsync(true);
            if (state != AutoStartState.Enabled)
            {
                suppress = true; startup.IsChecked = false; suppress = false;
                var blocked = state is AutoStartState.DisabledByUser or AutoStartState.DisabledByPolicy;
                startupHint.Visibility = openStartupApps.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                startupHint.Visibility = openStartupApps.Visibility = Visibility.Collapsed;
            }
        };
        startup.Unchecked += async (_, _) =>
        {
            if (suppress) return;
            await AutoStart.SetEnabledAsync(false);
        };
        // Tắt chạy nền thì cũng tắt khởi động cùng Windows.
        bg.Unchecked += (_, _) => { if (startup.IsChecked == true) startup.IsChecked = false; };
        shelf.Children.Add(startup);
        shelf.Children.Add(startupHint);
        shelf.Children.Add(openStartupApps);
        Loaded += async (_, _) =>
        {
            var current = await AutoStart.GetStateAsync();
            suppress = true; startup.IsChecked = current == AutoStartState.Enabled; suppress = false;
            if (current is AutoStartState.DisabledByUser or AutoStartState.DisabledByPolicy)
            {
                startupHint.Visibility = openStartupApps.Visibility = Visibility.Visible;
            }
        };

        // ---- Cập nhật ----
        var updates = Card(content, Loc.T("SettingsUpdates"));
        var autoCheck = Toggle(Loc.T("SettingsCheckUpdates"), _settings.CheckForUpdates,
            on => _settings.CheckForUpdates = on);
        updates.Children.Add(autoCheck);

        var urlCaption = Caption(Loc.T("SettingsUpdateUrl"));
        urlCaption.Margin = new Thickness(0, 12, 0, 4);
        updates.Children.Add(urlCaption);
        var url = new TextBox { Text = _settings.UpdateFeedUrl };
        AutomationProperties.SetName(url, "Update feed url");
        url.TextChanged += (_, _) => { _settings.UpdateFeedUrl = url.Text; SettingsStore.Save(_settings); };
        updates.Children.Add(url);

        var checkNow = new Ui.Button
        {
            Content = Loc.T("SettingsCheckUpdateNow"),
            Appearance = Ui.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0)
        };
        AutomationProperties.SetName(checkNow, "Check for updates now");
        updates.Children.Add(checkNow);

        var updateStatus = new TextBlock
        {
            Text = string.Empty,
            Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        AutomationProperties.SetName(updateStatus, "Update check result");
        updates.Children.Add(updateStatus);

        Ui.Button? downloadButton = null;
        checkNow.Click += async (_, _) =>
        {
            checkNow.IsEnabled = false;
            checkNow.Content = Loc.T("SettingsUpdateChecking");
            updateStatus.Text = Loc.T("SettingsUpdateChecking");

            if (downloadButton is not null)
            {
                updates.Children.Remove(downloadButton);
                downloadButton = null;
            }

            try
            {
                var result = await UpdateRunner.CheckAsync();
                updateStatus.Text = result.Message;

                if (result.Status == UpdateCheckStatus.UpdateAvailable
                    && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
                {
                    downloadButton = new Ui.Button
                    {
                        Content = Loc.T("UpdateDownload"),
                        Appearance = Ui.ControlAppearance.Primary,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 8, 0, 0)
                    };
                    downloadButton.Click += (_, _) =>
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = result.ReleaseUrl,
                                UseShellExecute = true
                            });
                        }
                        catch
                        {
                            // Bỏ qua nếu không mở được trình duyệt.
                        }
                    };
                    updates.Children.Add(downloadButton);
                }
            }
            finally
            {
                checkNow.IsEnabled = true;
                checkNow.Content = Loc.T("SettingsCheckUpdateNow");
            }
        };

        // Bản Store do Store tự cập nhật: các điều khiển trên chỉ dành cho bản portable.
        if (BFR.Core.ShellIntegration.IsPackaged)
        {
            foreach (var e in new UIElement[] { autoCheck, urlCaption, url, checkNow, updateStatus })
            {
                e.Visibility = Visibility.Collapsed;
            }
            var storeManaged = Caption(Loc.T("UpdateStoreManaged"));
            AutomationProperties.SetName(storeManaged, "Updates managed by Microsoft Store");
            updates.Children.Add(storeManaged);
        }

        // ---- Giấy phép ----
        var licenseCard = Card(content, Loc.T("SettingsLicense"));
        var status = _license.Status;
        var licenseText = status.IsTrial
            ? Loc.F("SettingsLicenseTrial", (int)Math.Ceiling((_license.License.TrialRemaining() ?? TimeSpan.Zero).TotalDays))
            : status.IsPro
                ? Loc.T("SettingsLicensePro")
                : Loc.T("SettingsLicenseFree");

        licenseCard.Children.Add(new TextBlock
        {
            Text = licenseText,
            Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap
        });

        if (!status.IsPro)
        {
            var upgrade = new Ui.Button
            {
                Content = Loc.T("Upgrade"),
                Appearance = Ui.ControlAppearance.Primary,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0)
            };
            upgrade.Click += (_, _) => UpgradeRequested?.Invoke(this, EventArgs.Empty);
            licenseCard.Children.Add(upgrade);
        }

        // ---- Quyền riêng tư & giới thiệu ----
        var about = Card(content, Loc.T("SettingsPrivacy") + " · " + Loc.T("SettingsAbout"));
        about.Children.Add(Caption(Loc.T("SettingsPrivacyHint")));

        // C8: thời hạn giữ lịch sử/journal và xoá dữ liệu hoạt động theo yêu cầu.
        var retentionCaption = Caption(Loc.T("SettingsRetention"));
        retentionCaption.Margin = new Thickness(0, 12, 0, 4);
        about.Children.Add(retentionCaption);

        var retention = new ComboBox { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(retention, "History retention");
        foreach (var days in AppSettings.RetentionChoices)
            retention.Items.Add(new ComboBoxItem { Content = Loc.F("SettingsRetentionDays", days), Tag = days.ToString() });
        SelectByTag(retention, AppSettings.NormalizeRetentionDays(_settings.HistoryRetentionDays).ToString());
        retention.SelectionChanged += (_, _) =>
        {
            if (retention.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var days))
            {
                _settings.HistoryRetentionDays = AppSettings.NormalizeRetentionDays(days);
                SettingsStore.Save(_settings);
                BFR.Core.HistoryStore.DefaultRetention = TimeSpan.FromDays(_settings.HistoryRetentionDays);
            }
        };
        about.Children.Add(retention);

        var erase = new Ui.Button
        {
            Content = Loc.T("SettingsEraseActivity"),
            Appearance = Ui.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0)
        };
        AutomationProperties.SetName(erase, "Clear history and undo data");
        var eraseStatus = Caption(string.Empty);
        eraseStatus.Margin = new Thickness(0, 6, 0, 0);
        erase.Click += (_, _) =>
        {
            var answer = MessageBox.Show(Window.GetWindow(this), Loc.T("SettingsEraseConfirm"),
                Loc.T("SettingsEraseActivity"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            var result = ActivityMaintenance.EraseAll();
            eraseStatus.Text = result.Failed == 0
                ? Loc.F("SettingsEraseDone", result.FilesDeleted)
                : Loc.F("SettingsEraseDoneFailed", result.FilesDeleted, result.Failed);
        };
        about.Children.Add(erase);
        about.Children.Add(eraseStatus);

        about.Children.Add(Caption(Loc.F("SettingsVersion", BFR.Core.AppVersion.Current)));

        var rateButton = new Ui.Button
        {
            Content = Loc.T("RateApp"),
            Appearance = Ui.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        AutomationProperties.SetName(rateButton, "Rate this app");
        rateButton.Click += async (_, _) =>
        {
            var owner = Window.GetWindow(this);
            var hwnd = owner is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            await BFR.Store.StoreRatingService.RequestRateAndReviewAsync(hwnd);
        };
        about.Children.Add(rateButton);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content.Root };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        Content = root;
    }

    /// <summary>Hai cột ô kính xếp xen kẽ (masonry); tự gập về 1 cột khi chiều rộng nhỏ hơn 740px.</summary>
    private sealed class TileColumns
    {
        private readonly StackPanel _left = new() { Margin = new Thickness(0, 0, 6, 0) };
        private readonly StackPanel _right = new() { Margin = new Thickness(6, 0, 0, 0) };
        private readonly ColumnDefinition _rightCol = new();
        private readonly System.Collections.Generic.List<UIElement> _tiles = new();
        private bool _singleColumn;

        public TileColumns()
        {
            Root = new Grid { Margin = new Thickness(28, 4, 28, 16), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
            Root.ColumnDefinitions.Add(new ColumnDefinition());
            Root.ColumnDefinitions.Add(_rightCol);
            Grid.SetColumn(_right, 1);
            Root.Children.Add(_left);
            Root.Children.Add(_right);
            Root.SizeChanged += (_, e) => UpdateLayout(e.NewSize.Width < 740);
        }

        public Grid Root { get; }

        public void Add(UIElement tile)
        {
            _tiles.Add(tile);
            if (_singleColumn || (_tiles.Count - 1) % 2 == 0) _left.Children.Add(tile);
            else _right.Children.Add(tile);
        }

        private void UpdateLayout(bool single)
        {
            if (single == _singleColumn) return;
            _singleColumn = single;
            _left.Children.Clear();
            _right.Children.Clear();

            if (_singleColumn)
            {
                _rightCol.Width = new GridLength(0);
                _left.Margin = new Thickness(0);
                _right.Visibility = Visibility.Collapsed;
                foreach (var t in _tiles) _left.Children.Add(t);
            }
            else
            {
                _rightCol.Width = new GridLength(1, GridUnitType.Star);
                _left.Margin = new Thickness(0, 0, 6, 0);
                _right.Visibility = Visibility.Visible;
                for (var i = 0; i < _tiles.Count; i++)
                    (i % 2 == 0 ? _left : _right).Children.Add(_tiles[i]);
            }
        }
    }

    /// <summary>Thêm một ô kính có tiêu đề vào <paramref name="parent"/> và trả về StackPanel bên trong.</summary>
    private static StackPanel Card(TileColumns parent, string title)
    {
        var inner = new StackPanel();
        parent.Add(new BFR.App.Controls.GlassTile
        {
            Header = title,
            Content = inner,
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(18, 16, 18, 16),
            CornerRadius = (CornerRadius)Application.Current.FindResource("BfrRadiusTileSm"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        });
        return inner;
    }

    /// <summary>Công tắc bật/tắt: lưu ngay vào cài đặt khi đổi.</summary>
    private Ui.ToggleSwitch Toggle(string text, bool isOn, Action<bool> set)
    {
        // Content là TextBlock có xuống dòng: ToggleSwitch không tự wrap chuỗi dài (tiếng Pháp, tiếng Trung).
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
        var toggle = new Ui.ToggleSwitch { Content = label, IsChecked = isOn };
        AutomationProperties.SetName(toggle, text);
        toggle.Checked += (_, _) => { set(true); SettingsStore.Save(_settings); SettingsChanged?.Invoke(this, EventArgs.Empty); };
        toggle.Unchecked += (_, _) => { set(false); SettingsStore.Save(_settings); SettingsChanged?.Invoke(this, EventArgs.Empty); };
        return toggle;
    }

    private static void SelectByTag(ComboBox combo, string? tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static string LanguageName(string code) => code switch
    {
        "en" => "English",
        "fr" => "Français",
        "zh-Hans" => "简体中文",
        _ => "Tiếng Việt"
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("CaptionTextStyle"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4)
    };
}
