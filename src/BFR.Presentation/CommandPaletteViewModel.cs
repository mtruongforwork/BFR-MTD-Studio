namespace BFR.Presentation;

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

/// <summary>Một lệnh trong Command Palette.</summary>
public sealed class PaletteCommand
{
    public PaletteCommand(string title, string category, Action execute, string? shortcut = null, string? icon = null)
    {
        Title = title;
        Category = category;
        Execute = execute;
        Shortcut = shortcut;
        Icon = icon;
    }

    /// <summary>Tên lệnh (đã địa phương hóa).</summary>
    public string Title { get; }

    /// <summary>Nhóm lệnh (ví dụ: Page, Tool, Preset, Rule, Action — đã địa phương hóa).</summary>
    public string Category { get; }

    /// <summary>Phím tắt hiển thị (ví dụ "Ctrl+K").</summary>
    public string? Shortcut { get; }

    /// <summary>Glyph biểu tượng tùy chọn (Segoe Fluent Icons).</summary>
    public string? Icon { get; }

    /// <summary>Hành động khi chạy lệnh.</summary>
    public Action Execute { get; }
}

/// <summary>Kho lệnh của Command Palette.</summary>
public sealed class CommandRegistry
{
    private readonly List<PaletteCommand> _commands = new();

    /// <summary>Tất cả lệnh đã đăng ký.</summary>
    public IReadOnlyList<PaletteCommand> Commands => _commands;

    /// <summary>Đăng ký một lệnh.</summary>
    public CommandRegistry Register(string title, string category, Action execute, string? shortcut = null, string? icon = null)
    {
        _commands.Add(new PaletteCommand(title, category, execute, shortcut, icon));
        return this;
    }
}

/// <summary>
/// ViewModel cho Command Palette (Ctrl+K): gõ để lọc, ↑↓ để chọn, Enter để chạy.
/// </summary>
public sealed class CommandPaletteViewModel : ObservableObject
{
    private readonly CommandRegistry _registry;
    private readonly IStringProvider _strings;
    private readonly IUiDispatcher _dispatcher;

    private string _query = string.Empty;
    private PaletteCommand? _selected;
    private int _selectedIndex;

    public CommandPaletteViewModel(CommandRegistry registry, IStringProvider strings, IUiDispatcher dispatcher)
    {
        _registry = registry;
        _strings = strings;
        _dispatcher = dispatcher;
        Filtered = new ObservableCollection<PaletteCommand>();
        Refilter();
    }

    /// <summary>Các lệnh khớp với từ khoá.</summary>
    public ObservableCollection<PaletteCommand> Filtered { get; }

    /// <summary>Nhãn tìm kiếm.</summary>
    public string SearchHint => _strings.T("CommandPaletteSearch");

    /// <summary>Gợi ý phím tắt.</summary>
    public string HintText => _strings.T("CommandPaletteHint");

    /// <summary>Thông báo khi không có kết quả.</summary>
    public string EmptyText => _strings.T("CommandPaletteEmpty");

    /// <summary>True khi không có lệnh nào khớp.</summary>
    public bool HasResults => Filtered.Count > 0;

    /// <summary>Từ khoá tìm kiếm.</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value)) Refilter();
        }
    }

    /// <summary>Lệnh đang chọn.</summary>
    public PaletteCommand? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(SelectedIndex));
        }
    }

    /// <summary>Chỉ số của lệnh đang chọn (dùng để highlight).</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (!Set(ref _selectedIndex, value)) return;
            Raise(nameof(Selected));
        }
    }

    /// <summary>Di chuyển vùng chọn lên/xuống.</summary>
    public void MoveSelection(int delta)
    {
        if (Filtered.Count == 0) return;

        var index = _selectedIndex + delta;
        if (index < 0) index = Filtered.Count - 1;
        if (index >= Filtered.Count) index = 0;

        _selectedIndex = index;
        Raise(nameof(SelectedIndex));
        Selected = Filtered[index];
    }

    /// <summary>Chạy lệnh đang chọn (trả về false nếu không có lệnh nào).</summary>
    public bool ExecuteSelected()
    {
        var command = Selected ?? Filtered.FirstOrDefault();
        if (command is null) return false;

        command.Execute();
        return true;
    }

    /// <summary>Chạy trực tiếp một lệnh cụ thể (dùng khi click/double-click chuột).</summary>
    public bool Execute(PaletteCommand? command)
    {
        if (command is null) return false;

        Selected = command;
        command.Execute();
        return true;
    }

    private void Refilter()
    {
        var query = _query?.Trim() ?? string.Empty;

        List<PaletteCommand> matches;
        if (query.Length == 0)
        {
            matches = _registry.Commands.ToList();
        }
        else
        {
            matches = _registry.Commands
                .Select(c => (Command: c, Score: Score(c, query)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Command.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.Command)
                .ToList();
        }

        _dispatcher.Post(() =>
        {
            Filtered.Clear();
            foreach (var command in matches)
            {
                Filtered.Add(command);
            }

            _selectedIndex = Filtered.Count > 0 ? 0 : -1;
            Selected = Filtered.FirstOrDefault();
            Raise(nameof(HasResults));
        });
    }

    /// <summary>
    /// Chấm điểm mờ (fuzzy scoring) cho lệnh: ưu tiên khớp chính xác, tiền tố, đầu từ,
    /// chữ viết tắt (acronym), chuỗi con, và khớp theo nhóm (category). Trả về &lt;= 0 nếu không khớp.
    /// </summary>
    public static int Score(PaletteCommand command, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 1;

        var needle = Normalize(query.Trim());
        if (needle.Length == 0) return 1;

        var titleNorm = Normalize(command.Title);
        var catNorm = Normalize(command.Category);
        var compactNeedle = needle.Replace(" ", string.Empty);

        // 1. Khớp chính xác tiêu đề.
        if (string.Equals(titleNorm, needle, StringComparison.Ordinal))
        {
            return 1000;
        }

        // 2. Khớp tiền tố tiêu đề.
        if (titleNorm.StartsWith(needle, StringComparison.Ordinal))
        {
            return 850 - Math.Min(titleNorm.Length, 100);
        }

        // 3. Khớp tiền tố đầu từ trong tiêu đề.
        var titleWords = titleNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < titleWords.Length; i++)
        {
            if (titleWords[i].StartsWith(needle, StringComparison.Ordinal))
            {
                return 700 - i * 10;
            }
        }

        // 4. Khớp chữ viết tắt (acronym) từ chữ cái đầu mỗi từ ("ttf" → "Thêm tệp").
        if (compactNeedle.Length > 0 && titleWords.Length > 0)
        {
            var initials = string.Concat(titleWords.Select(w => w[0]));
            if (string.Equals(initials, compactNeedle, StringComparison.Ordinal))
            {
                return 620;
            }
            if (initials.StartsWith(compactNeedle, StringComparison.Ordinal))
            {
                return 540;
            }
            if (initials.Contains(compactNeedle, StringComparison.Ordinal))
            {
                return 460;
            }
        }

        // 5. Khớp chuỗi con trong tiêu đề.
        var idx = titleNorm.IndexOf(needle, StringComparison.Ordinal);
        if (idx >= 0)
        {
            return 420 - Math.Min(idx, 100);
        }

        // 6. Khớp chuỗi con không khoảng trắng trong tiêu đề.
        if (compactNeedle.Length > 0)
        {
            var compactTitle = titleNorm.Replace(" ", string.Empty);
            var compactIdx = compactTitle.IndexOf(compactNeedle, StringComparison.Ordinal);
            if (compactIdx >= 0)
            {
                return 340 - Math.Min(compactIdx, 100);
            }
        }

        // 7. Khớp nhóm (category) hoặc kết hợp tiêu đề + nhóm.
        if (catNorm.StartsWith(needle, StringComparison.Ordinal))
        {
            return 240;
        }
        if (catNorm.Contains(needle, StringComparison.Ordinal))
        {
            return 180;
        }

        var combined = titleNorm + " " + catNorm;
        if (combined.Contains(needle, StringComparison.Ordinal))
        {
            return 150;
        }

        if (compactNeedle.Length > 0 && combined.Replace(" ", string.Empty).Contains(compactNeedle, StringComparison.Ordinal))
        {
            return 120;
        }

        // 8. Khớp dãy con mờ (subsequence) trên tiêu đề (yêu cầu >= 2 ký tự để tránh nhiễu).
        if (compactNeedle.Length >= 2)
        {
            int fuzzy = SubsequenceScore(titleNorm, compactNeedle);
            if (fuzzy > 0) return fuzzy;
        }

        return 0;
    }

    private static int SubsequenceScore(string text, string pattern)
    {
        int pi = 0;
        int consecutive = 0;
        int bonus = 0;
        int firstMatch = -1;

        for (int ti = 0; ti < text.Length && pi < pattern.Length; ti++)
        {
            if (text[ti] == pattern[pi])
            {
                if (firstMatch < 0) firstMatch = ti;
                consecutive++;
                bonus += consecutive * 6;
                if (ti == 0 || text[ti - 1] == ' ') bonus += 12;
                pi++;
            }
            else
            {
                consecutive = 0;
            }
        }

        if (pi < pattern.Length) return 0;

        return Math.Max(10, 80 + bonus - firstMatch);
    }

    private static string Normalize(string value)
    {
        // Bỏ dấu để gõ không dấu vẫn tìm được ("lich" → "lịch").
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }

        return builder
            .ToString()
            .Normalize(NormalizationForm.FormC)
            .Replace("đ", "d")
            .Replace("Đ", "D")
            .ToLowerInvariant();
    }
}
