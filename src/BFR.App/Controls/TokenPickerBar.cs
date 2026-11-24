namespace BFR.App.Controls;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

/// <summary>
/// Thanh chọn token dạng chip bấm chọn phân nhóm (Basic, Date, Media, Hash).
/// Nhấp vào chip sẽ chèn token vào đúng vị trí con trỏ (CaretIndex) của <see cref="TargetTextBox"/>.
/// </summary>
public sealed class TokenPickerBar : StackPanel
{
    public static readonly DependencyProperty TargetTextBoxProperty =
        DependencyProperty.Register(nameof(TargetTextBox), typeof(TextBox), typeof(TokenPickerBar), new PropertyMetadata(null));

    private sealed record TokenDef(string Token, string DescKey, string Example);
    private sealed record TokenGroup(string Key, string FallbackTitle, TokenDef[] Tokens);

    private static readonly TokenGroup[] Groups =
    {
        new("TokenGroupBasic", "Basic", new[]
        {
            new TokenDef("{name}", "TokenDescName", "photo"),
            new TokenDef("{ext}", "TokenDescExt", ".jpg"),
            new TokenDef("{n:3}", "TokenDescSeq", "001"),
            new TokenDef("{parent}", "TokenDescParent", "Vacation"),
            new TokenDef("{parent:2}", "TokenDescParent2", "2026")
        }),
        new("TokenGroupDate", "Date", new[]
        {
            new TokenDef("{date:yyyy-MM-dd}", "TokenDescDate", "2026-05-14"),
            new TokenDef("{modified:yyyyMMdd}", "TokenDescModified", "20260514"),
            new TokenDef("{created:yyyy-MM-dd}", "TokenDescCreated", "2026-05-01"),
            new TokenDef("{taken:yyyy-MM-dd}", "TokenDescTaken", "2026-05-14")
        }),
        new("TokenGroupMedia", "Media", new[]
        {
            new TokenDef("{width}", "TokenDescWidth", "1920"),
            new TokenDef("{height}", "TokenDescHeight", "1080"),
            new TokenDef("{camera}", "TokenDescCamera", "iPhone 15 Pro"),
            new TokenDef("{artist}", "TokenDescArtist", "Artist"),
            new TokenDef("{album}", "TokenDescAlbum", "Album"),
            new TokenDef("{title}", "TokenDescTitle", "Track Title"),
            new TokenDef("{track}", "TokenDescTrack", "01"),
            new TokenDef("{year}", "TokenDescYear", "2026")
        }),
        new("TokenGroupHash", "Hash", new[]
        {
            new TokenDef("{md5:8}", "TokenDescMd5", "a1b2c3d4"),
            new TokenDef("{sha256:8}", "TokenDescSha256", "9f86d081")
        })
    };

    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly List<Button> _tabButtons = new();
    private int _activeGroup;

    public TokenPickerBar()
    {
        Orientation = Orientation.Vertical;
        Margin = new Thickness(0, 6, 0, 2);

        Children.Add(_tabs);
        Children.Add(_chips);

        BuildTabs();
        SelectGroup(0);
    }

    public TokenPickerBar(TextBox target) : this()
    {
        TargetTextBox = target;
    }

    /// <summary>Ô nhập liệu nhận token khi người dùng bấm chip.</summary>
    public TextBox? TargetTextBox
    {
        get => (TextBox?)GetValue(TargetTextBoxProperty);
        set => SetValue(TargetTextBoxProperty, value);
    }

    /// <summary>Phát ra sau khi người dùng bấm một chip token.</summary>
    public event Action<string>? TokenInserted;

    private static string T(string key, string fallback)
    {
        var value = Application.Current?.TryFindResource(key) as string;
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private void BuildTabs()
    {
        _tabs.Children.Clear();
        _tabButtons.Clear();

        for (var i = 0; i < Groups.Length; i++)
        {
            var idx = i;
            var group = Groups[i];
            var btn = new Button
            {
                Content = T(group.Key, group.FallbackTitle),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 4, 0),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                Focusable = false,
                BorderThickness = new Thickness(1)
            };
            btn.Click += (_, _) => SelectGroup(idx);
            _tabButtons.Add(btn);
            _tabs.Children.Add(btn);
        }
    }

    private void SelectGroup(int index)
    {
        _activeGroup = index;
        for (var i = 0; i < _tabButtons.Count; i++)
        {
            var active = i == _activeGroup;
            var btn = _tabButtons[i];
            btn.SetResourceReference(Control.BackgroundProperty, active ? "ChipSelectedBackground" : "ChipBackground");
            btn.SetResourceReference(Control.BorderBrushProperty, active ? "ChipSelectedBorder" : "ChipBorder");
            btn.SetResourceReference(Control.ForegroundProperty, active ? "BfrAccFg" : "TextSecondary");
        }

        _chips.Children.Clear();
        foreach (var def in Groups[_activeGroup].Tokens)
        {
            var token = def.Token;
            var desc = T(def.DescKey, def.Token);
            var chip = new Button
            {
                Content = token,
                ToolTip = $"{desc}  ({T("TokenExamplePrefix", "e.g.")} {def.Example})",
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                FontSize = 11.5,
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 5, 5),
                Cursor = Cursors.Hand,
                Focusable = false,
                BorderThickness = new Thickness(1)
            };
            chip.SetResourceReference(Control.BackgroundProperty, "ChipBackground");
            chip.SetResourceReference(Control.BorderBrushProperty, "ChipBorder");
            chip.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
            chip.Click += (_, _) => InsertToken(token);
            _chips.Children.Add(chip);
        }
    }

    private void InsertToken(string token)
    {
        if (TargetTextBox is { } box)
        {
            var text = box.Text ?? string.Empty;
            var start = Math.Clamp(box.SelectionStart, 0, text.Length);
            var len = Math.Clamp(box.SelectionLength, 0, text.Length - start);
            box.Text = text.Remove(start, len).Insert(start, token);
            box.CaretIndex = start + token.Length;
            box.Focus();
        }

        TokenInserted?.Invoke(token);
    }
}
