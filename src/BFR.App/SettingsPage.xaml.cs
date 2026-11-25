namespace BFR.App;

using BFR.App.Controls.Mac;
using BFR.Core.Licensing;
using BFR.App.Infrastructure;
using BFR.Presentation.Design;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

/// <summary>
/// Nội dung cửa sổ Cài đặt kiểu macOS (v3.2): hàng tab biểu tượng (Chung · Giao diện · Phím tắt &amp; Chuột ·
/// Menu tròn · Pro · Giới thiệu), mỗi tab là các nhóm "inset grouped" với nhãn bên trái, điều khiển bên phải.
/// Mọi thay đổi lưu ngay.
/// </summary>
public class SettingsPage : PageBase
{
    /// <summary>Các tab theo thứ tự hiển thị: mã, khoá chuỗi, biểu tượng.</summary>
    public static readonly IReadOnlyList<(string Id, string TitleKey, string Icon)> Tabs = new[]
    {
        ("general", "MacSettingsGeneral", "Settings"),
        ("appearance", "MacSettingsAppearance", "Sun"),
        ("shortcuts", "MacSettingsShortcuts", "Keyboard"),
        ("radial", "MacSettingsRadial", "CircleDot"),
        ("pro", "MacSettingsPro", "Star"),
        ("about", "MacSettingsAbout", "Info")
    };

    /// <summary>Tab mở lần trước, giữ qua các lần dựng lại (đổi ngôn ngữ, đổi giao diện).</summary>
    private static string _lastSection = "general";

    /// <summary>Người dùng bấm "Nâng cấp" trong tab Pro.</summary>
    public event EventHandler? UpgradeRequested;

    /// <summary>Cửa sổ Cài đặt đóng: cửa sổ chính áp dụng các thay đổi (khay, hook, phím tắt...).</summary>
    public event EventHandler? SettingsLeft;

    /// <summary>Một cài đặt vừa đổi: cửa sổ chính áp dụng ngay (phím tắt, lắc chuột, chạy nền) thay vì đợi đóng cửa sổ.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Đổi tab: cửa sổ chứa chỉnh lại chiều cao.</summary>
    public event EventHandler? SectionChanged;

    public override void OnHidden() => SettingsLeft?.Invoke(this, EventArgs.Empty);

    private readonly LicenseCoordinator _license;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, StackPanel> _sections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RadioButton> _tabButtons = new(StringComparer.Ordinal);
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    public SettingsPage(LicenseCoordinator license)
    {
        _license = license;
        _settings = SettingsStore.Load();

        AutomationProperties.SetName(this, "Settings window");
        SetResourceReference(FontFamilyProperty, "Mac.Font");
        SetResourceReference(ForegroundProperty, "Mac.Label");
        FontSize = 13;

        var host = new Grid { Margin = new Thickness(24, 16, 24, 20) };
        foreach (var (id, _, _) in Tabs)
        {
            var section = new StackPanel { Visibility = Visibility.Collapsed };
            _sections[id] = section;
            host.Children.Add(section);
        }

        BuildGeneral(_sections["general"]);
        BuildAppearance(_sections["appearance"]);
        BuildShortcuts(_sections["shortcuts"]);
        BuildRadial(_sections["radial"]);
        BuildPro(_sections["pro"]);
        BuildAbout(_sections["about"]);

        _scroll.SetResourceReference(StyleProperty, "Mac.ScrollViewer");
        _scroll.Content = host;

        var root = new DockPanel();
        var strip = BuildTabStrip();
        DockPanel.SetDock(strip, Dock.Top);
        root.Children.Add(strip);
        root.Children.Add(_scroll);
        Content = root;

        Show(_sections.ContainsKey(_lastSection) ? _lastSection : "general");
    }

    public string CurrentSection { get; private set; } = "general";

    /// <summary>Mở một tab theo mã trong <see cref="Tabs"/>; mã lạ thì bỏ qua.</summary>
    public void Show(string id)
    {
        if (!_sections.ContainsKey(id)) return;
        var changed = CurrentSection != id;
        CurrentSection = _lastSection = id;
        foreach (var (key, section) in _sections) section.Visibility = key == id ? Visibility.Visible : Visibility.Collapsed;
        _tabButtons[id].IsChecked = true;
        _scroll.ScrollToTop();
        if (changed && IsLoaded) Motion.FadeIn(_sections[id], MotionKind.Standard);
        SectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private UIElement BuildTabStrip()
    {
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 0, 8, 8) };
        foreach (var (id, titleKey, icon) in Tabs)
        {
            var glyph = new MacIcon { Size = 20, Margin = new Thickness(0, 0, 0, 3) };
            glyph.SetResourceReference(MacIcon.DataProperty, "Icon." + icon);
            var content = new StackPanel();
            content.Children.Add(glyph);
            content.Children.Add(new TextBlock { Text = Loc.T(titleKey), HorizontalAlignment = HorizontalAlignment.Center });

            var button = new RadioButton { Content = content, GroupName = "settingsTabs" };
            button.SetResourceReference(StyleProperty, "Mac.SettingsTab");
            AutomationProperties.SetName(button, Loc.T(titleKey));
            AutomationProperties.SetAutomationId(button, "Settings.Tab." + id);
            var chosen = id;
            button.Checked += (_, _) =>
            {
                if (CurrentSection != chosen) Show(chosen);
            };
            _tabButtons[id] = button;
            row.Children.Add(button);
        }

        var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
        line.SetResourceReference(Border.BorderBrushProperty, "Mac.Separator");
        return line;
    }

    // ---------------------------------------------------------------- Chung

    private void BuildGeneral(StackPanel page)
    {
        var language = Combo("Language selector");
        foreach (var code in new[] { "vi", "en", "fr", "zh-Hans" })
        {
            language.Items.Add(new ComboBoxItem { Content = LanguageName(code), Tag = code });
        }
        SelectByTag(language, _settings.Language);
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedItem is ComboBoxItem { Tag: string code })
            {
                _settings.Language = code;
                SettingsStore.Save(_settings);
                Loc.Apply(code);
            }
        };
        Group(page, null).Add(Row(Loc.T("MacSettingsLanguageLabel"), language));

        // ---- Chạy nền ----
        var background = Group(page, null);
        var bg = Switch(Loc.T("SettingsRunInBackground"), _settings.RunInBackground, on => _settings.RunInBackground = on);
        background.Add(SwitchRow(bg, null));

        // Khởi động cùng Windows: chỉ có nghĩa khi bật chạy nền (ứng dụng khởi động ẩn trong khay).
        var startupHint = Hint(Loc.T("SettingsStartupBlocked"));
        startupHint.Visibility = Visibility.Collapsed;
        var openStartupApps = MacDetailPane.CreateButton(Loc.T("SettingsOpenStartupApps"));
        openStartupApps.HorizontalAlignment = HorizontalAlignment.Left;
        openStartupApps.Margin = new Thickness(0, 6, 0, 0);
        openStartupApps.Visibility = Visibility.Collapsed;
        openStartupApps.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); }
            catch { /* không mở được trang cài đặt: bỏ qua */ }
        };

        var suppress = false;   // đặt IsChecked bằng code không được kích hoạt lại logic bật/tắt
        var startup = Switch(Loc.T("SettingsStartWithWindows"), false, _ => { });
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
        var startupBlock = new StackPanel();
        startupBlock.Children.Add(SwitchRow(startup, null));
        startupBlock.Children.Add(startupHint);
        startupBlock.Children.Add(openStartupApps);
        background.Add(startupBlock);
        Loaded += async (_, _) =>
        {
            var current = await AutoStart.GetStateAsync();
            suppress = true; startup.IsChecked = current == AutoStartState.Enabled; suppress = false;
            if (current is AutoStartState.DisabledByUser or AutoStartState.DisabledByPolicy)
            {
                startupHint.Visibility = openStartupApps.Visibility = Visibility.Visible;
            }
        };

        BuildUpdates(page);
    }

    private void BuildUpdates(StackPanel page)
    {
        var updates = Group(page, Loc.T("SettingsUpdates"));

        // Bản Store do Store tự cập nhật: các điều khiển cập nhật chỉ dành cho bản portable.
        if (BFR.Core.ShellIntegration.IsPackaged)
        {
            var storeManaged = Hint(Loc.T("UpdateStoreManaged"));
            storeManaged.Margin = new Thickness(0, 10, 0, 10);
            AutomationProperties.SetName(storeManaged, "Updates managed by Microsoft Store");
            updates.Add(storeManaged);
            return;
        }

        updates.Add(SwitchRow(Switch(Loc.T("SettingsCheckUpdates"), _settings.CheckForUpdates, on => _settings.CheckForUpdates = on), null));

        var url = new TextBox { Text = _settings.UpdateFeedUrl, Margin = new Thickness(0, 4, 0, 0) };
        url.SetResourceReference(StyleProperty, "Mac.TextBox");
        AutomationProperties.SetName(url, "Update feed url");
        url.TextChanged += (_, _) => { _settings.UpdateFeedUrl = url.Text; SettingsStore.Save(_settings); };
        var urlBlock = new StackPanel { Margin = new Thickness(0, 8, 0, 10) };
        urlBlock.Children.Add(Label(Loc.T("SettingsUpdateUrl")));
        urlBlock.Children.Add(url);
        updates.Add(urlBlock);

        var checkNow = MacDetailPane.CreateButton(Loc.T("SettingsCheckUpdateNow"), automationName: "Check for updates now");
        checkNow.HorizontalAlignment = HorizontalAlignment.Left;
        var updateStatus = Hint(string.Empty);
        updateStatus.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetName(updateStatus, "Update check result");
        var checkBlock = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        checkBlock.Children.Add(checkNow);
        checkBlock.Children.Add(updateStatus);
        updates.Add(checkBlock);

        Button? downloadButton = null;
        checkNow.Click += async (_, _) =>
        {
            checkNow.IsEnabled = false;
            checkNow.Content = Loc.T("SettingsUpdateChecking");
            updateStatus.Text = Loc.T("SettingsUpdateChecking");

            if (downloadButton is not null)
            {
                checkBlock.Children.Remove(downloadButton);
                downloadButton = null;
            }

            try
            {
                var result = await UpdateRunner.CheckAsync();
                updateStatus.Text = result.Message;

                if (result.Status == UpdateCheckStatus.UpdateAvailable
                    && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
                {
                    downloadButton = MacDetailPane.CreateButton(Loc.T("UpdateDownload"), "Mac.Button.Primary");
                    downloadButton.HorizontalAlignment = HorizontalAlignment.Left;
                    downloadButton.Margin = new Thickness(0, 8, 0, 0);
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
                    checkBlock.Children.Add(downloadButton);
                    SectionChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            finally
            {
                checkNow.IsEnabled = true;
                checkNow.Content = Loc.T("SettingsCheckUpdateNow");
            }
        };
    }

    // ---------------------------------------------------------------- Giao diện

    private void BuildAppearance(StackPanel page)
    {
        var look = Group(page, null);

        var theme = Combo("Theme selector");
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
        look.Add(Row(Loc.T("MacSettingsThemeLabel"), theme));

        // Màu nhấn: chấm tròn kiểu macOS, tên màu hiện ở tooltip; áp dụng ngay.
        var dots = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var savedAccent = MacAccents.Parse(_settings.AccentPalette);
        foreach (var accent in MacAccents.All)
        {
            var label = Loc.T("MacAccent" + accent.Id);
            var option = new RadioButton
            {
                GroupName = "accent",
                Background = MacTheme.Solid(accent.Tint(ThemeManager.IsDark)),
                ToolTip = label,
                IsChecked = accent.Id == savedAccent.Id
            };
            option.SetResourceReference(StyleProperty, "Mac.AccentDot");
            AutomationProperties.SetName(option, label);
            AutomationProperties.SetAutomationId(option, "Accent." + accent.Id);
            var chosen = accent.Id;
            option.Checked += (_, _) =>
            {
                if (string.Equals(_settings.AccentPalette, chosen, StringComparison.OrdinalIgnoreCase)) return;
                _settings.AccentPalette = chosen;
                SettingsStore.Save(_settings);
                ThemeManager.Apply(ThemeManager.Current);
            };
            dots.Children.Add(option);
        }
        look.Add(Row(Loc.T("SettingsAccent"), dots));

        var effectsGroup = Group(page, null);

        effectsGroup.Add(SwitchRow(Switch(Loc.T("SettingsMascot"), _settings.ShowMascot, on =>
        {
            _settings.ShowMascot = on;
            SettingsStore.Save(_settings);
            SettingsStore.NotifyMascotVisibilityChanged(on);
        }), null));

        // Giảm chuyển động: có hiệu lực ngay (Windows tắt hiệu ứng hoặc tương phản cao cũng tự bật).
        effectsGroup.Add(SwitchRow(Switch(Loc.T("SettingsReduceMotion"), _settings.ReduceMotion, on =>
        {
            _settings.ReduceMotion = on;
            Motion.UserReduced = on;
        }), Loc.T("SettingsReduceMotionHint")));

        // Giảm hiệu ứng (quyết định 6.2): áp dụng ngay, không cần khởi động lại.
        var effects = Switch(Loc.T("SettingsReduceEffects"), _settings.ReduceEffects, on =>
        {
            _settings.ReduceEffects = on;
            SettingsStore.Save(_settings);
            ThemeManager.Apply(ThemeManager.Current);
        });
        AutomationProperties.SetName(effects, "Reduce visual effects");
        effectsGroup.Add(SwitchRow(effects,
            BfrTheme.NoHardwareAcceleration ? Loc.T("SettingsReduceEffectsAuto") : Loc.T("SettingsReduceEffectsHint")));
    }

    // ---------------------------------------------------------------- Phím tắt & Chuột

    private void BuildShortcuts(StackPanel page)
    {
        var shake = Group(page, Loc.T("SettingsShelf"));
        shake.Add(SwitchRow(Switch(Loc.T("SettingsShake"), _settings.ShakeToOpenShelf, on => _settings.ShakeToOpenShelf = on),
            Loc.T("SettingsShakeHint")));

        var hotkeys = Group(page, null);
        hotkeys.Add(SwitchRow(Switch(Loc.T("SettingsHotkey"), _settings.ShelfHotkeyEnabled, on => _settings.ShelfHotkeyEnabled = on),
            Loc.T("SettingsHotkeyHint")));
        var hotkeyBox = HotkeyCombo("Shelf hotkey", _settings.ShelfHotkey, BFR.Core.HotkeySpec.Default, chosen => _settings.ShelfHotkey = chosen);
        hotkeys.Add(Row(Loc.T("MacSettingsKeyCombination"), hotkeyBox));

        var radial = Group(page, null);
        radial.Add(SwitchRow(Switch(Loc.T("SettingsRadial"), _settings.RadialHotkeyEnabled, on => _settings.RadialHotkeyEnabled = on),
            Loc.T("SettingsRadialHint")));
        var radialBox = HotkeyCombo("Radial menu hotkey", _settings.RadialHotkey, "Ctrl+Alt+Q", chosen => _settings.RadialHotkey = chosen);
        radial.Add(Row(Loc.T("MacSettingsKeyCombination"), radialBox));
    }

    private ComboBox HotkeyCombo(string name, string saved, string fallback, Action<string> set)
    {
        var box = Combo(name);
        foreach (var h in BFR.Core.HotkeySpec.Presets) box.Items.Add(h);
        box.SelectedItem = BFR.Core.HotkeySpec.TryParse(saved, out var current) ? current.ToString() : fallback;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not string chosen) return;
            set(chosen);
            SettingsStore.Save(_settings);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        };
        return box;
    }

    // ---------------------------------------------------------------- Menu tròn

    private void BuildRadial(StackPanel page)
    {
        var orb = Group(page, null);
        orb.Add(SwitchRow(Switch(
            Application.Current?.TryFindResource("SettingsFloatingOrb") as string ?? "Hiện nút tròn nổi trên Desktop & mọi ứng dụng",
            _settings.ShowFloatingOrb,
            on => _settings.ShowFloatingOrb = on),
            Application.Current?.TryFindResource("SettingsFloatingOrbHint") as string ?? "Nút tròn kiểu AssistiveTouch luôn nổi; bấm hoặc kéo-thả tệp vào để bung Menu tròn 8 chức năng."));

        // Giữ chuột giữa mở Menu Tròn ngữ cảnh (v3.0).
        var middle = Group(page, null);
        middle.Add(SwitchRow(Switch(Loc.T("SettingsMiddleMouseRadial"), _settings.MiddleMouseRadialEnabled, on => _settings.MiddleMouseRadialEnabled = on),
            Loc.T("SettingsMiddleMouseRadialHint")));

        var hold = Combo("Middle mouse hold duration");
        foreach (var ms in new[] { 150, 220, 350, 500 })
            hold.Items.Add(new ComboBoxItem { Content = $"{ms} ms", Tag = ms.ToString() });
        SelectByTag(hold, AppSettings.NormalizeMiddleMouseHoldMs(_settings.MiddleMouseHoldMs).ToString());
        hold.SelectionChanged += (_, _) =>
        {
            if (hold.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var ms))
            {
                _settings.MiddleMouseHoldMs = AppSettings.NormalizeMiddleMouseHoldMs(ms);
                SettingsStore.Save(_settings);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        middle.Add(Row(Loc.T("MacSettingsHoldDuration"), hold));
    }

    // ---------------------------------------------------------------- Pro

    private void BuildPro(StackPanel page)
    {
        var status = _license.Status;
        var licenseText = status.IsTrial
            ? Loc.F("SettingsLicenseTrial", (int)Math.Ceiling((_license.License.TrialRemaining() ?? TimeSpan.Zero).TotalDays))
            : status.IsPro
                ? Loc.T("SettingsLicensePro")
                : Loc.T("SettingsLicenseFree");

        var block = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
        block.Children.Add(Label(licenseText));
        if (!status.IsPro)
        {
            var upgrade = MacDetailPane.CreateButton(Loc.T("Upgrade"), "Mac.Button.Primary");
            upgrade.HorizontalAlignment = HorizontalAlignment.Left;
            upgrade.Margin = new Thickness(0, 10, 0, 0);
            upgrade.Click += (_, _) => UpgradeRequested?.Invoke(this, EventArgs.Empty);
            block.Children.Add(upgrade);
        }

        Group(page, Loc.T("SettingsLicense")).Add(block);
    }

    // ---------------------------------------------------------------- Giới thiệu và quyền riêng tư

    private void BuildAbout(StackPanel page)
    {
        var privacy = Group(page, Loc.T("SettingsPrivacy"));
        var privacyText = Hint(Loc.T("SettingsPrivacyHint"));
        privacyText.Margin = new Thickness(0, 10, 0, 10);
        privacy.Add(privacyText);

        // C8: thời hạn giữ lịch sử/journal và xoá dữ liệu hoạt động theo yêu cầu.
        var retention = Combo("History retention");
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
        privacy.Add(Row(Loc.T("SettingsRetention"), retention));

        var erase = MacDetailPane.CreateButton(Loc.T("SettingsEraseActivity"), "Mac.Button.Destructive", "Clear history and undo data");
        erase.HorizontalAlignment = HorizontalAlignment.Left;
        var eraseStatus = Hint(string.Empty);
        eraseStatus.Margin = new Thickness(0, 6, 0, 0);
        erase.Click += (_, _) =>
        {
            var answer = MacAlert.Show(Window.GetWindow(this), Loc.T("SettingsEraseConfirm"),
                Loc.T("SettingsEraseActivity"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            var result = ActivityMaintenance.EraseAll();
            eraseStatus.Text = result.Failed == 0
                ? Loc.F("SettingsEraseDone", result.FilesDeleted)
                : Loc.F("SettingsEraseDoneFailed", result.FilesDeleted, result.Failed);
        };
        var eraseBlock = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        eraseBlock.Children.Add(erase);
        eraseBlock.Children.Add(eraseStatus);
        privacy.Add(eraseBlock);

        var about = Group(page, Loc.T("SettingsAbout"));
        var rate = MacDetailPane.CreateButton(Loc.T("RateApp"), automationName: "Rate this app");
        rate.Click += async (_, _) =>
        {
            var owner = Window.GetWindow(this);
            var hwnd = owner is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            await BFR.Store.StoreRatingService.RequestRateAndReviewAsync(hwnd);
        };
        about.Add(Row(Loc.F("SettingsVersion", BFR.Core.AppVersion.Current), rate));
    }

    // ---------------------------------------------------------------- Khối dựng chung

    /// <summary>Nhóm "inset grouped": các hàng cách nhau bằng đường kẻ mảnh.</summary>
    private sealed class GroupBox
    {
        private readonly StackPanel _rows;

        public GroupBox(StackPanel rows) => _rows = rows;

        public void Add(FrameworkElement row)
        {
            if (_rows.Children.Count > 0)
            {
                var line = new Border();
                line.SetResourceReference(StyleProperty, "Mac.RowLine");
                _rows.Children.Add(line);
            }

            _rows.Children.Add(row);
        }
    }

    private static GroupBox Group(StackPanel page, string? title)
    {
        if (title is not null)
        {
            var heading = new TextBlock { Text = title, Margin = new Thickness(2, page.Children.Count == 0 ? 0 : 6, 0, 6) };
            heading.SetResourceReference(StyleProperty, "Mac.Section");
            page.Children.Add(heading);
        }

        var rows = new StackPanel();
        var border = new Border { Child = rows, Margin = new Thickness(0, 0, 0, 12) };
        border.SetResourceReference(StyleProperty, "Mac.Group");
        page.Children.Add(border);
        return new GroupBox(rows);
    }

    private static Grid Row(string label, FrameworkElement control)
    {
        var grid = new Grid { MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 12, 6) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label");
        grid.Children.Add(text);

        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(0, 6, 0, 6);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>Hàng công tắc: nhãn và gợi ý bên trái, công tắc bên phải.</summary>
    private static Grid SwitchRow(CheckBox toggle, string? hint)
    {
        var grid = new Grid { MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 12, 8) };
        var label = new TextBlock { Text = (string)toggle.Tag, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label");
        // Bấm vào nhãn cũng bật/tắt như hàng cài đặt của macOS.
        label.MouseLeftButtonUp += (_, _) => { if (toggle.IsEnabled) toggle.IsChecked = toggle.IsChecked != true; };
        texts.Children.Add(label);
        if (!string.IsNullOrEmpty(hint)) texts.Children.Add(Hint(hint));
        grid.Children.Add(texts);

        toggle.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return grid;
    }

    /// <summary>Công tắc bật/tắt: lưu ngay vào cài đặt khi đổi.</summary>
    private CheckBox Switch(string text, bool isOn, Action<bool> set)
    {
        var toggle = new CheckBox { Tag = text, IsChecked = isOn };
        toggle.SetResourceReference(StyleProperty, "Mac.Switch");
        AutomationProperties.SetName(toggle, text);
        toggle.Checked += (_, _) => { set(true); SettingsStore.Save(_settings); SettingsChanged?.Invoke(this, EventArgs.Empty); };
        toggle.Unchecked += (_, _) => { set(false); SettingsStore.Save(_settings); SettingsChanged?.Invoke(this, EventArgs.Empty); };
        return toggle;
    }

    private static ComboBox Combo(string automationName)
    {
        var combo = new ComboBox { Width = 220 };
        combo.SetResourceReference(StyleProperty, "Mac.ComboBox");
        AutomationProperties.SetName(combo, automationName);
        return combo;
    }

    private static TextBlock Label(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label");
        return block;
    }

    private static TextBlock Hint(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Mac.Label3");
        return block;
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
}
