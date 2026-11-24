namespace BFR.Shell;

using BFR.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>
/// <c>IEnumExplorerCommand</c> phía .NET (CCW): Explorer dùng nó để duyệt các lệnh con của menu
/// "Batch Rename with BFR". Dùng con trỏ thô cho mảng kết quả để không phụ thuộc vào marshaller.
/// </summary>
[ComVisible(true)]
[Guid("a88826f8-186f-4987-aade-ea0cef8fbfe8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumExplorerCommandServer
{
    /// <summary>Lấy tối đa <paramref name="celt"/> lệnh; ghi con trỏ IExplorerCommand (đã AddRef) vào <paramref name="rgelt"/>.</summary>
    [PreserveSig]
    int Next(uint celt, IntPtr rgelt, IntPtr pceltFetched);

    [PreserveSig]
    int Skip(uint celt);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Clone(out IntPtr ppenum);
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class EnumExplorerCommand : IEnumExplorerCommandServer
{
    private readonly IReadOnlyList<IExplorerCommandServer> _commands;
    private int _index;

    public EnumExplorerCommand(IReadOnlyList<IExplorerCommandServer> commands, int index = 0)
    {
        _commands = commands;
        _index = index;
    }

    public int Next(uint celt, IntPtr rgelt, IntPtr pceltFetched)
    {
        ComServerLifetime.Touch();
        if (rgelt == IntPtr.Zero) return HResult.E_INVALIDARG;

        uint fetched = 0;
        while (fetched < celt && _index < _commands.Count)
        {
            var unknown = Marshal.GetComInterfaceForObject(_commands[_index], typeof(IExplorerCommandServer));
            Marshal.WriteIntPtr(rgelt, (int)fetched * IntPtr.Size, unknown);   // người gọi sở hữu một tham chiếu
            _index++;
            fetched++;
        }

        if (pceltFetched != IntPtr.Zero) Marshal.WriteInt32(pceltFetched, (int)fetched);
        return fetched == celt ? HResult.S_OK : HResult.S_FALSE;
    }

    public int Skip(uint celt)
    {
        ComServerLifetime.Touch();
        _index = (int)Math.Min((long)_index + celt, _commands.Count);
        return _index < _commands.Count ? HResult.S_OK : HResult.S_FALSE;
    }

    public int Reset()
    {
        ComServerLifetime.Touch();
        _index = 0;
        return HResult.S_OK;
    }

    public int Clone(out IntPtr ppenum)
    {
        ComServerLifetime.Touch();
        ppenum = Marshal.GetComInterfaceForObject(new EnumExplorerCommand(_commands, _index), typeof(IEnumExplorerCommandServer));
        return HResult.S_OK;
    }
}

/// <summary>
/// Điều kiện hiển thị của một lệnh con theo loại tệp đang chọn: chỉ hiện khi có ít nhất <see cref="MinMatches"/> tệp
/// có đuôi trong <see cref="Extensions"/>. Giúp menu chuột phải của tệp .txt/.jpg không bị chèn các lệnh PDF.
/// </summary>
public sealed record ExplorerCommandFilter(IReadOnlyList<string> Extensions, int MinMatches = 1)
{
    /// <summary>Chỉ xem tối đa chừng này mục đầu của lựa chọn khi quyết định ẩn/hiện (menu phải mở tức thì).</summary>
    public const int MaxItemsInspected = 500;

    public bool Matches(IReadOnlyList<string> paths)
    {
        var matches = 0;
        foreach (var path in paths)
        {
            var ext = Path.GetExtension(path);
            if (ext.Length == 0) continue;
            foreach (var allowed in Extensions)
            {
                if (!ext.Equals(allowed, StringComparison.OrdinalIgnoreCase)) continue;
                if (++matches >= MinMatches) return true;
                break;
            }
        }

        return matches >= MinMatches;
    }
}

/// <summary>
/// Một lệnh con của menu: "Mở trong BFR" (khi <see cref="QuickKey"/> null) hoặc một preset đổi tên nhanh
/// (chạy <c>BatchFileRenamer.exe --quick &lt;khoá&gt;</c>, áp dụng ngay không mở cửa sổ chính).
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ExplorerSubCommand : IExplorerCommandServer
{
    private readonly Guid _canonicalName;
    private readonly string _titleVi;
    private readonly string _titleEn;

    /// <summary>ECS_HIDDEN: Explorer không hiển thị lệnh.</summary>
    internal const uint StateHidden = 0x2;

    public ExplorerSubCommand(
        Guid canonicalName, string titleVi, string titleEn, string? quickKey, string? toolKey = null, bool isRadial = false,
        ExplorerCommandFilter? filter = null)
    {
        ToolKey = toolKey;
        IsRadial = isRadial;
        Filter = filter;
        _canonicalName = canonicalName;
        _titleVi = titleVi;
        _titleEn = titleEn;
        QuickKey = quickKey;
    }

    public string? QuickKey { get; }

    /// <summary>Điều kiện hiển thị theo loại tệp; null = luôn hiện.</summary>
    public ExplorerCommandFilter? Filter { get; }

    /// <summary>Khoá công cụ tệp: mở trang Công cụ với các tệp đã chọn.</summary>
    public string? ToolKey { get; }

    /// <summary>True để mở Menu Tròn ngữ cảnh BFR v3.0 tại con trỏ (<c>--radial</c>).</summary>
    public bool IsRadial { get; }

    public string Title(bool vietnamese) => vietnamese ? _titleVi : _titleEn;

    private static bool IsVietnamese() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("vi", StringComparison.OrdinalIgnoreCase);

    public int GetTitle(IShellItemArray? psiItemArray, out IntPtr ppszName)
    {
        ComServerLifetime.Touch();
        ppszName = Marshal.StringToCoTaskMemUni(Title(IsVietnamese()));
        return HResult.S_OK;
    }

    public int GetIcon(IShellItemArray? psiItemArray, out IntPtr ppszIcon)
    {
        ComServerLifetime.Touch();
        ppszIcon = IntPtr.Zero;   // lệnh con không có biểu tượng riêng
        return HResult.E_NOTIMPL;
    }

    public int GetToolTip(IShellItemArray? psiItemArray, out IntPtr ppszInfotip)
    {
        ComServerLifetime.Touch();
        ppszInfotip = IntPtr.Zero;
        return HResult.E_NOTIMPL;
    }

    public int GetCanonicalName(out Guid pguidCommandName)
    {
        ComServerLifetime.Touch();
        pguidCommandName = _canonicalName;
        return HResult.S_OK;
    }

    public int GetState(IShellItemArray? psiItemArray, int fOkToBeSlow, out uint pCmdState)
    {
        ComServerLifetime.Touch();
        pCmdState = 0;   // ECS_ENABLED
        if (Filter is null) return HResult.S_OK;

        try
        {
            // Bấm vào nền thư mục (không có mục nào) hoặc không có tệp phù hợp: ẩn lệnh theo loại tệp.
            var paths = ExplorerCommand.CollectPaths(psiItemArray, ExplorerCommandFilter.MaxItemsInspected);
            if (!Filter.Matches(paths)) pCmdState = StateHidden;
        }
        catch
        {
            pCmdState = 0;   // không đọc được lựa chọn: hiện lệnh (công cụ tự báo "không có tệp phù hợp") thay vì làm hỏng menu
        }

        return HResult.S_OK;
    }

    public int Invoke(IShellItemArray? psiItemArray, IntPtr pbc)
    {
        ComServerLifetime.Touch();
        try
        {
            var paths = ExplorerCommand.CollectPaths(psiItemArray);
            return ExplorerCommandLauncher.Launch(paths, QuickKey, ToolKey, IsRadial);
        }
        catch
        {
            return HResult.E_FAIL;
        }
    }

    public int GetFlags(out uint pFlags)
    {
        ComServerLifetime.Touch();
        pFlags = 0;   // ECF_DEFAULT
        return HResult.S_OK;
    }

    public int EnumSubCommands(out IntPtr ppEnum)
    {
        ComServerLifetime.Touch();
        ppEnum = IntPtr.Zero;
        return HResult.E_NOTIMPL;
    }

    /// <summary>Danh sách lệnh con theo thứ tự hiển thị: Mở trong BFR, rồi các preset nhanh.</summary>
    public static IReadOnlyList<IExplorerCommandServer> BuildAll()
    {
        var list = new List<IExplorerCommandServer>
        {
            new ExplorerSubCommand(Guid.Parse("b7a3c5d1-0001-4e5f-8a6b-0f1e2d3c4b01"), "Mở trong BFR", "Open in BFR", null),
            new ExplorerSubCommand(Guid.Parse("b7a3c5d1-00ff-4e5f-8a6b-0f1e2d3c4b01"), "Mở Menu Tròn BFR tại đây", "Open BFR Radial Menu", null, isRadial: true)
        };

        var id = 2;
        foreach (var quick in QuickPresets.All)
        {
            list.Add(new ExplorerSubCommand(
                Guid.Parse($"b7a3c5d1-{id++:x4}-4e5f-8a6b-0f1e2d3c4b01"),
                quick.TitleVi,
                quick.TitleEn,
                quick.Key));
        }

        // Công cụ tệp v2.0: mở trang Công cụ với các tệp đã chọn (người dùng xem trước rồi mới áp dụng).
        var tools = new (string Key, string Vi, string En)[]
        {
            ("organize", "Sắp xếp vào thư mục…", "Organize into folders…"),
            ("duplicates", "Tìm tệp trùng…", "Find duplicates…"),
            ("metadata", "Ngày giờ / xoá GPS…", "Dates / remove GPS…"),
            ("archive", "Nén ZIP…", "Compress to ZIP…"),
            ("checksum", "Tính checksum…", "Compute checksum…")
        };
        foreach (var (key, vi, en) in tools)
        {
            list.Add(new ExplorerSubCommand(
                Guid.Parse($"b7a3c5d1-{id++:x4}-4e5f-8a6b-0f1e2d3c4b01"), vi, en, null, key));
        }

        // Công cụ PDF/Docs v3.0: chỉ hiện khi lựa chọn có tệp phù hợp. Luôn mở trang Công cụ để xem trước rồi mới áp dụng
        // (có journal, hoàn tác được). Thêm CUỐI danh sách để GUID của các lệnh cũ không đổi.
        var pdf = new[] { ".pdf" };
        var office = new[] { ".docx", ".xlsx", ".pptx" };
        var toPdf = new[] { ".docx", ".xlsx", ".pptx", ".md", ".markdown", ".txt", ".csv", ".tsv" };
        var docTools = new (string Key, string Vi, string En, ExplorerCommandFilter Filter)[]
        {
            ("pdf-compress", "Nén PDF…", "Compress PDF…", new ExplorerCommandFilter(pdf)),
            ("pdf-merge", "Gộp PDF…", "Merge PDFs…", new ExplorerCommandFilter(pdf, MinMatches: 2)),
            ("doc-to-pdf", "Xuất sang PDF…", "Convert to PDF…", new ExplorerCommandFilter(toPdf)),
            ("doc-clean-meta", "Xoá dữ liệu ẩn tài liệu Office…", "Remove hidden Office data…", new ExplorerCommandFilter(office))
        };
        foreach (var (key, vi, en, filter) in docTools)
        {
            list.Add(new ExplorerSubCommand(
                Guid.Parse($"b7a3c5d1-{id++:x4}-4e5f-8a6b-0f1e2d3c4b01"), vi, en, null, key, filter: filter));
        }

        return list;
    }
}
