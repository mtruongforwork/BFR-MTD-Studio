namespace BFR.App.Controls.Mac;

using System;
using System.Windows;

/// <summary>
/// Phần của khung cửa sổ mà một trang muốn lấp: điều khiển trên toolbar, bảng inspector bên phải và dòng trạng thái.
/// Trang trả null cho chỗ không dùng. Mỗi phần tử chỉ được gắn vào một chỗ nên trang phải giữ lại, không tạo mới mỗi lần đọc.
/// </summary>
public interface IMacShellContent
{
    FrameworkElement? ToolbarCenter { get; }

    FrameworkElement? ToolbarTrailing { get; }

    FrameworkElement? Inspector { get; }

    string? StatusText { get; }

    /// <summary>Phát khi một trong các phần trên thay đổi.</summary>
    event EventHandler? ShellContentChanged;
}
